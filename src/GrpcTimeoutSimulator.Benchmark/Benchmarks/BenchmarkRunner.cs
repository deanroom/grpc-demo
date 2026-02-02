using GrpcTimeoutSimulator.Benchmark.Hosting;
using GrpcTimeoutSimulator.Benchmark.Reporting;

namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

/// <summary>
/// 基准测试运行器
/// </summary>
public class BenchmarkRunner
{
    private readonly BenchmarkConfig _config;
    private readonly ConsoleReporter _reporter;

    public BenchmarkRunner(BenchmarkConfig config)
    {
        _config = config;
        _reporter = new ConsoleReporter(config.Sla);
    }

    /// <summary>
    /// 运行基准测试
    /// </summary>
    public async Task<BenchmarkReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var report = new BenchmarkReport
        {
            StartTime = DateTime.UtcNow,
            Sla = _config.Sla
        };

        _reporter.PrintHeader();

        EmbeddedServer? server = null;
        string serverAddress;

        try
        {
            // 启动服务端
            if (string.IsNullOrEmpty(_config.ExternalServerAddress))
            {
                _reporter.PrintPhaseHeader("启动内嵌服务端", "");

                var serverOptions = new EmbeddedServerOptions
                {
                    Port = _config.Server.Port,
                    MinWorkerThreads = _config.Server.MinWorkerThreads,
                    MinIoThreads = _config.Server.MinIoThreads,
                    MaxStreamsPerConnection = _config.Server.MaxStreamsPerConnection,
                    MinProcessingTimeUs = _config.Server.MinProcessingTimeUs,
                    MaxProcessingTimeMs = _config.Server.MaxProcessingTimeMs
                };

                server = await EmbeddedServer.StartAsync(serverOptions);
                serverAddress = server.ServerAddress;
                report.IsEmbeddedServer = true;

                _reporter.PrintServerStarted(serverAddress);
            }
            else
            {
                serverAddress = _config.ExternalServerAddress;
                report.IsEmbeddedServer = false;
            }

            report.ServerAddress = serverAddress;
            _reporter.PrintConfig(serverAddress, report.IsEmbeddedServer);

            // 创建负载生成器
            using var loadGenerator = new SteadyStateLoadGenerator(
                serverAddress,
                _config.Client,
                _config.Probe.RequestTimeoutMs);

            if (_config.Mode == BenchmarkMode.Auto)
            {
                // 自动探测模式
                var prober = new ConcurrencyProber(_config, loadGenerator, server, _reporter);
                var probeResult = await prober.ProbeAsync(cancellationToken);
                report.ProbeResult = probeResult;

                _reporter.PrintProbeSummary(probeResult);
            }
            else
            {
                // 手动测试模式
                var results = await RunManualTestsAsync(loadGenerator, server, cancellationToken);
                report.ManualResults = results;

                _reporter.PrintManualResults(results);
            }
        }
        finally
        {
            if (server != null)
            {
                await server.DisposeAsync();
            }
        }

        report.EndTime = DateTime.UtcNow;

        Console.WriteLine();
        Console.WriteLine($"  总耗时: {(report.EndTime - report.StartTime).TotalSeconds:F1} 秒");
        Console.WriteLine();

        return report;
    }

    /// <summary>
    /// 运行手动测试
    /// </summary>
    private async Task<List<ConcurrencyTestResult>> RunManualTestsAsync(
        SteadyStateLoadGenerator loadGenerator,
        EmbeddedServer? server,
        CancellationToken cancellationToken)
    {
        var results = new List<ConcurrencyTestResult>();

        _reporter.PrintPhaseHeader("手动测试模式", $"测试 {string.Join(", ", _config.ManualConcurrencyLevels)} 并发");

        // 预热
        _reporter.PrintInfo("预热中...");
        server?.ResetStats();
        await loadGenerator.RunAsync(
            _config.Probe.WarmupConcurrency,
            _config.Probe.WarmupDurationSec,
            cancellationToken);

        foreach (var concurrency in _config.ManualConcurrencyLevels)
        {
            if (cancellationToken.IsCancellationRequested) break;

            server?.ResetStats();

            var loadResult = await loadGenerator.RunAsync(
                concurrency,
                _config.Probe.TestDurationSec,
                cancellationToken);

            var latencyDist = LatencyDistribution.Calculate(loadResult.Latencies);

            var testResult = new ConcurrencyTestResult
            {
                Concurrency = concurrency,
                DurationSec = loadResult.DurationSec,
                TotalRequests = loadResult.TotalRequests,
                SuccessCount = loadResult.SuccessCount,
                TimeoutCount = loadResult.TimeoutCount,
                ErrorCount = loadResult.ErrorCount,
                Latency = latencyDist
            };

            // 检查 SLA
            var successRateMet = testResult.SuccessRate >= _config.Sla.SuccessRate;
            var p99Met = testResult.Latency.P99 <= _config.Sla.P99ThresholdMs;
            testResult.MeetsSla = successRateMet && p99Met;

            if (!testResult.MeetsSla)
            {
                var reasons = new List<string>();
                if (!successRateMet)
                    reasons.Add($"成功率 {testResult.SuccessRate:P2} < {_config.Sla.SuccessRate:P1}");
                if (!p99Met)
                    reasons.Add($"P99 {testResult.Latency.P99:F0}ms > {_config.Sla.P99ThresholdMs}ms");
                testResult.SlaViolationReason = string.Join(", ", reasons);
            }

            results.Add(testResult);
            _reporter.PrintTestResult(testResult);
        }

        return results;
    }
}
