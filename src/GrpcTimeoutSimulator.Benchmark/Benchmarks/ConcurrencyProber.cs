using System.Diagnostics;
using GrpcTimeoutSimulator.Benchmark.Hosting;
using GrpcTimeoutSimulator.Benchmark.Reporting;

namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

/// <summary>
/// 并发探测器
/// 采用自适应二分搜索算法探测最大并发能力
/// </summary>
public class ConcurrencyProber
{
    private readonly BenchmarkConfig _config;
    private readonly SteadyStateLoadGenerator _loadGenerator;
    private readonly EmbeddedServer? _server;
    private readonly ConsoleReporter _reporter;

    public ConcurrencyProber(
        BenchmarkConfig config,
        SteadyStateLoadGenerator loadGenerator,
        EmbeddedServer? server,
        ConsoleReporter reporter)
    {
        _config = config;
        _loadGenerator = loadGenerator;
        _server = server;
        _reporter = reporter;
    }

    /// <summary>
    /// 执行并发探测
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var result = new ProbeResult();
        var stopwatch = Stopwatch.StartNew();

        // Phase 1: 预热
        _reporter.PrintPhaseHeader("预热阶段", $"{_config.Probe.WarmupConcurrency} 并发 × {_config.Probe.WarmupDurationSec}秒");
        await WarmupAsync(cancellationToken);
        _reporter.PrintInfo("完成，建立连接池");

        // Phase 2: 指数增长
        _reporter.PrintPhaseHeader("指数增长阶段", "");
        var (lastGoodConcurrency, firstBadConcurrency) = await ExponentialGrowthPhaseAsync(result, cancellationToken);

        if (lastGoodConcurrency == 0)
        {
            _reporter.PrintError("无法找到满足 SLA 的并发级别，请检查服务端配置");
            result.TotalDurationSec = stopwatch.Elapsed.TotalSeconds;
            return result;
        }

        // Phase 3: 二分搜索
        if (firstBadConcurrency > lastGoodConcurrency + 10)
        {
            _reporter.PrintPhaseHeader("二分搜索阶段", "");
            lastGoodConcurrency = await BinarySearchPhaseAsync(lastGoodConcurrency, firstBadConcurrency, result, cancellationToken);
        }

        // Phase 4: 稳定性验证
        _reporter.PrintPhaseHeader("稳定性验证", $"{lastGoodConcurrency} 并发 × {_config.Probe.StabilityDurationSec}秒");
        var stabilityResult = await VerifyStabilityAsync(lastGoodConcurrency, cancellationToken);
        result.StabilityResult = stabilityResult;

        if (stabilityResult.MeetsSla)
        {
            _reporter.PrintInfo($"结果: 成功率 {stabilityResult.SuccessRate:P2}, P99 稳定在 {stabilityResult.Latency.P99:F0}ms");
        }
        else
        {
            _reporter.PrintWarning($"稳定性验证未通过: {stabilityResult.SlaViolationReason}");
            // 降低推荐并发数
            lastGoodConcurrency = (int)(lastGoodConcurrency * 0.9);
        }

        // 设置最终结果
        result.MaxConcurrency = lastGoodConcurrency;
        result.EffectiveConcurrency = FindEffectiveConcurrency(result.AllResults);
        result.SaturatedThroughput = FindSaturatedThroughput(result.AllResults, result.EffectiveConcurrency);

        stopwatch.Stop();
        result.TotalDurationSec = stopwatch.Elapsed.TotalSeconds;

        return result;
    }

    /// <summary>
    /// 预热阶段
    /// </summary>
    private async Task WarmupAsync(CancellationToken cancellationToken)
    {
        _server?.ResetStats();
        await _loadGenerator.RunAsync(
            _config.Probe.WarmupConcurrency,
            _config.Probe.WarmupDurationSec,
            cancellationToken);
        _server?.ResetStats();
    }

    /// <summary>
    /// 指数增长阶段
    /// </summary>
    private async Task<(int lastGood, int firstBad)> ExponentialGrowthPhaseAsync(
        ProbeResult result, CancellationToken cancellationToken)
    {
        int concurrency = _config.Probe.InitialConcurrency;
        int lastGoodConcurrency = 0;
        int firstBadConcurrency = _config.Probe.MaxConcurrency;

        while (concurrency <= _config.Probe.MaxConcurrency && !cancellationToken.IsCancellationRequested)
        {
            var testResult = await TestConcurrencyLevelAsync(concurrency, cancellationToken);
            result.AllResults.Add(testResult);

            _reporter.PrintTestResult(testResult);

            if (testResult.MeetsSla)
            {
                lastGoodConcurrency = concurrency;
                concurrency *= 2; // 指数增长
            }
            else
            {
                firstBadConcurrency = concurrency;
                break;
            }
        }

        return (lastGoodConcurrency, firstBadConcurrency);
    }

    /// <summary>
    /// 二分搜索阶段
    /// </summary>
    private async Task<int> BinarySearchPhaseAsync(
        int low, int high, ProbeResult result, CancellationToken cancellationToken)
    {
        int lastGood = low;

        while (high - low > 10 && !cancellationToken.IsCancellationRequested)
        {
            int mid = (low + high) / 2;

            var testResult = await TestConcurrencyLevelAsync(mid, cancellationToken);
            result.AllResults.Add(testResult);

            _reporter.PrintTestResult(testResult);

            if (testResult.MeetsSla)
            {
                lastGood = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return lastGood;
    }

    /// <summary>
    /// 稳定性验证阶段
    /// </summary>
    private async Task<ConcurrencyTestResult> VerifyStabilityAsync(int concurrency, CancellationToken cancellationToken)
    {
        _server?.ResetStats();

        var loadResult = await _loadGenerator.RunAsync(
            concurrency,
            _config.Probe.StabilityDurationSec,
            cancellationToken);

        return CreateTestResult(concurrency, loadResult);
    }

    /// <summary>
    /// 测试单个并发级别
    /// </summary>
    private async Task<ConcurrencyTestResult> TestConcurrencyLevelAsync(int concurrency, CancellationToken cancellationToken)
    {
        _server?.ResetStats();

        var loadResult = await _loadGenerator.RunAsync(
            concurrency,
            _config.Probe.TestDurationSec,
            cancellationToken);

        return CreateTestResult(concurrency, loadResult);
    }

    /// <summary>
    /// 创建测试结果
    /// </summary>
    private ConcurrencyTestResult CreateTestResult(int concurrency, LoadTestResult loadResult)
    {
        var latencyDist = LatencyDistribution.Calculate(loadResult.Latencies);

        var testResult = new ConcurrencyTestResult
        {
            Concurrency = concurrency,
            DurationSec = loadResult.DurationSec,
            TotalRequests = loadResult.TotalRequests,
            SuccessCount = loadResult.SuccessCount,
            TimeoutCount = loadResult.TimeoutCount,
            ErrorCount = loadResult.ErrorCount,
            Latency = latencyDist,
        };

        // 收集超时分析
        testResult.Timeout = new TimeoutAnalysis
        {
            Http2LayerTimeoutCount = loadResult.Http2LayerTimeoutCount,
            ServerLayerTimeoutCount = loadResult.ServerLayerTimeoutCount,
            AvgQueueWaitMs = loadResult.AvgQueueWaitMs,
            P99QueueWaitMs = loadResult.P99QueueWaitMs
        };

        // 收集资源快照
        if (_server != null)
        {
            var gcCounts = _server.Diagnostics.GetGcCounts();
            var threadStats = _server.Diagnostics.GetMinThreadPoolStats();

            testResult.Resource = new ResourceSnapshot
            {
                PeakQueueDepth = _server.Processor.PeakQueueDepth,
                MaxQueueWaitTimeMs = _server.Processor.MaxQueueWaitTimeMs,
                GcGen0Count = gcCounts.gen0,
                GcGen1Count = gcCounts.gen1,
                GcGen2Count = gcCounts.gen2,
                MinAvailableWorkerThreads = threadStats.minWorker,
                MinAvailableIoThreads = threadStats.minIo
            };
        }

        // 检查 SLA
        CheckSla(testResult);

        return testResult;
    }

    /// <summary>
    /// 检查是否满足 SLA
    /// </summary>
    private void CheckSla(ConcurrencyTestResult result)
    {
        var successRateMet = result.SuccessRate >= _config.Sla.SuccessRate;
        var p99Met = result.Latency.P99 <= _config.Sla.P99ThresholdMs;

        result.MeetsSla = successRateMet && p99Met;

        if (!result.MeetsSla)
        {
            var reasons = new List<string>();
            if (!successRateMet)
            {
                reasons.Add($"成功率 {result.SuccessRate:P2} < {_config.Sla.SuccessRate:P1}");
            }
            if (!p99Met)
            {
                reasons.Add($"P99 {result.Latency.P99:F0}ms > {_config.Sla.P99ThresholdMs}ms");
            }
            result.SlaViolationReason = string.Join(", ", reasons);
        }
    }

    /// <summary>
    /// 找到有效并发数（同时满足成功率和 P99）
    /// </summary>
    private int FindEffectiveConcurrency(List<ConcurrencyTestResult> results)
    {
        return results
            .Where(r => r.MeetsSla)
            .OrderByDescending(r => r.Concurrency)
            .FirstOrDefault()?.Concurrency ?? 0;
    }

    /// <summary>
    /// 找到饱和吞吐量
    /// </summary>
    private double FindSaturatedThroughput(List<ConcurrencyTestResult> results, int effectiveConcurrency)
    {
        return results
            .Where(r => r.Concurrency == effectiveConcurrency)
            .FirstOrDefault()?.Throughput ?? 0;
    }
}
