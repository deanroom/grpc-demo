using System.Collections.Concurrent;
using GrpcTimeoutSimulator.Client.LoadGenerators;

namespace GrpcTimeoutSimulator.Client.Diagnostics;

/// <summary>
/// 超时原因
/// </summary>
[Flags]
public enum TimeoutReason
{
    None = 0,
    QueueWaitTooLong = 1,        // 队列等待过长
    ProcessingTooLong = 2,       // 处理时间过长
    GcPause = 4,                 // GC 暂停
    ThreadPoolSaturation = 8,   // 线程池饱和
    NetworkLatency = 16          // 网络延迟
}

/// <summary>
/// 超时分析结果
/// </summary>
public class TimeoutAnalysis
{
    public required string RequestId { get; init; }
    public TimeoutReason Reasons { get; set; }
    public double TotalTimeMs { get; set; }
    public double QueueWaitTimeMs { get; set; }
    public double ProcessingTimeMs { get; set; }
    public long GcDurationMs { get; set; }
    public int AvailableWorkerThreads { get; set; }
    public int QueueDepth { get; set; }
}

/// <summary>
/// 客户端诊断系统
/// </summary>
public class ClientDiagnostics
{
    private readonly ConcurrentBag<RequestResult> _allResults = new();
    private readonly ConcurrentBag<TimeoutAnalysis> _timeoutAnalyses = new();
    private readonly object _consoleLock = new();
    private int _successCount;
    private int _timeoutCount;
    private int _errorCount;
    private int _lastReportedBurst;

    // 统计数据
    private int _queueWaitTimeoutCount;
    private int _processingTimeoutCount;
    private int _gcTimeoutCount;
    private int _threadPoolTimeoutCount;
    private int _peakQueueDepth;
    private double _maxQueueWaitTimeMs;

    /// <summary>
    /// 记录请求结果
    /// </summary>
    public void RecordResult(RequestResult result)
    {
        _allResults.Add(result);

        if (result.Success)
        {
            Interlocked.Increment(ref _successCount);
        }
        else if (result.IsTimeout)
        {
            Interlocked.Increment(ref _timeoutCount);

            // 分析超时原因
            var analysis = AnalyzeTimeout(result);
            _timeoutAnalyses.Add(analysis);

            // 实时输出超时信息
            PrintTimeoutRealtime(result, analysis);
        }
        else
        {
            Interlocked.Increment(ref _errorCount);
        }

        // 更新峰值队列深度
        if (result.QueueDepthAtEnqueue > _peakQueueDepth)
            _peakQueueDepth = result.QueueDepthAtEnqueue;
    }

    /// <summary>
    /// 分析超时原因
    /// </summary>
    private TimeoutAnalysis AnalyzeTimeout(RequestResult result)
    {
        var analysis = new TimeoutAnalysis
        {
            RequestId = result.RequestId,
            TotalTimeMs = result.TotalTimeMs,
            QueueDepth = result.QueueDepthAtEnqueue
        };

        // 如果有服务端时间线，进行详细分析
        if (result.ServerTimeline != null)
        {
            double queueWaitMs = (result.ServerTimeline.DequeueTimeTicks - result.ServerTimeline.EnqueueTimeTicks)
                / (double)TimeSpan.TicksPerMillisecond;
            double processingMs = (result.ServerTimeline.CompleteTimeTicks - result.ServerTimeline.DequeueTimeTicks)
                / (double)TimeSpan.TicksPerMillisecond;

            analysis.QueueWaitTimeMs = queueWaitMs;
            analysis.ProcessingTimeMs = processingMs;

            // 更新最大队列等待时间
            if (queueWaitMs > _maxQueueWaitTimeMs)
                _maxQueueWaitTimeMs = queueWaitMs;

            // 判定规则 1: 队列等待过长 (> 50% 总耗时)
            if (queueWaitMs > result.TotalTimeMs * 0.5)
            {
                analysis.Reasons |= TimeoutReason.QueueWaitTooLong;
                Interlocked.Increment(ref _queueWaitTimeoutCount);
            }

            // 判定规则 2: 处理时间过长 (> 50% 总耗时)
            if (processingMs > result.TotalTimeMs * 0.5)
            {
                analysis.Reasons |= TimeoutReason.ProcessingTooLong;
                Interlocked.Increment(ref _processingTimeoutCount);
            }
        }

        // 如果有诊断信息
        if (result.DiagnosticInfo != null)
        {
            analysis.AvailableWorkerThreads = result.DiagnosticInfo.AvailableWorkerThreads;
            analysis.GcDurationMs = result.DiagnosticInfo.GcDurationMs;

            // 判定规则 3: GC 暂停 (GC 耗时 > 100ms)
            if (result.DiagnosticInfo.GcOccurred && result.DiagnosticInfo.GcDurationMs > 100)
            {
                analysis.Reasons |= TimeoutReason.GcPause;
                Interlocked.Increment(ref _gcTimeoutCount);
            }

            // 判定规则 4: 线程池饱和 (可用线程 < 5)
            if (result.DiagnosticInfo.AvailableWorkerThreads < 5)
            {
                analysis.Reasons |= TimeoutReason.ThreadPoolSaturation;
                Interlocked.Increment(ref _threadPoolTimeoutCount);
            }
        }

        // 如果没有服务端数据，可能是网络问题或请求未到达服务端
        if (result.ServerTimeline == null && analysis.Reasons == TimeoutReason.None)
        {
            analysis.Reasons = TimeoutReason.NetworkLatency;
        }

        return analysis;
    }

    /// <summary>
    /// 实时打印超时信息
    /// </summary>
    private void PrintTimeoutRealtime(RequestResult result, TimeoutAnalysis analysis)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {result.RequestId} TIMEOUT ({result.TotalTimeMs:F0}ms)");
            Console.ResetColor();

            Console.Write("  原因: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(FormatTimeoutReasons(analysis.Reasons));
            Console.ResetColor();

            var details = new List<string>();
            if (analysis.QueueWaitTimeMs > 0)
                details.Add($"队列等待={analysis.QueueWaitTimeMs:F0}ms");
            if (analysis.ProcessingTimeMs > 0)
                details.Add($"处理时间={analysis.ProcessingTimeMs:F0}ms");
            if (analysis.QueueDepth > 0)
                details.Add($"队列深度={analysis.QueueDepth}");
            if (analysis.GcDurationMs > 0)
                details.Add($"GC={analysis.GcDurationMs}ms");
            if (analysis.AvailableWorkerThreads > 0)
                details.Add($"可用线程={analysis.AvailableWorkerThreads}");

            if (details.Count > 0)
            {
                Console.WriteLine($"  详情: {string.Join(", ", details)}");
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// 格式化超时原因
    /// </summary>
    private static string FormatTimeoutReasons(TimeoutReason reasons)
    {
        if (reasons == TimeoutReason.None)
            return "未知";

        var parts = new List<string>();
        if (reasons.HasFlag(TimeoutReason.QueueWaitTooLong))
            parts.Add("队列等待过长");
        if (reasons.HasFlag(TimeoutReason.ProcessingTooLong))
            parts.Add("处理时间过长");
        if (reasons.HasFlag(TimeoutReason.GcPause))
            parts.Add("GC暂停");
        if (reasons.HasFlag(TimeoutReason.ThreadPoolSaturation))
            parts.Add("线程池饱和");
        if (reasons.HasFlag(TimeoutReason.NetworkLatency))
            parts.Add("网络延迟/未到达服务端");

        return string.Join(" + ", parts);
    }

    /// <summary>
    /// 打印单轮突发统计
    /// </summary>
    public void PrintBurstSummary(int burstNumber)
    {
        lock (_consoleLock)
        {
            int startIndex = _lastReportedBurst;
            int currentSuccess = _successCount;
            int currentTimeout = _timeoutCount;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  第 {burstNumber} 轮完成: 成功 {currentSuccess - startIndex} / 超时 {currentTimeout}");
            Console.ResetColor();

            _lastReportedBurst = currentSuccess + currentTimeout + _errorCount;
        }
    }

    /// <summary>
    /// 打印最终汇总报告
    /// </summary>
    public void PrintFinalReport()
    {
        lock (_consoleLock)
        {
            int total = _successCount + _timeoutCount + _errorCount;

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("======== 仿真运行报告 ========");
            Console.ResetColor();

            Console.WriteLine($"总请求数: {total}");
            Console.WriteLine($"成功: {_successCount} ({(total > 0 ? _successCount * 100.0 / total : 0):F1}%)");
            Console.ForegroundColor = _timeoutCount > 0 ? ConsoleColor.Red : ConsoleColor.Green;
            Console.WriteLine($"超时: {_timeoutCount} ({(total > 0 ? _timeoutCount * 100.0 / total : 0):F1}%)");
            Console.ResetColor();
            if (_errorCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"其他错误: {_errorCount} ({_errorCount * 100.0 / total:F1}%)");
                Console.ResetColor();
            }

            if (_timeoutCount > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("超时原因分布:");
                Console.ResetColor();
                Console.WriteLine($"  队列等待过长:     {_queueWaitTimeoutCount} ({_queueWaitTimeoutCount * 100.0 / _timeoutCount:F1}%)");
                Console.WriteLine($"  处理时间过长:     {_processingTimeoutCount} ({_processingTimeoutCount * 100.0 / _timeoutCount:F1}%)");
                Console.WriteLine($"  GC暂停:          {_gcTimeoutCount} ({_gcTimeoutCount * 100.0 / _timeoutCount:F1}%)");
                Console.WriteLine($"  线程池饱和:       {_threadPoolTimeoutCount} ({_threadPoolTimeoutCount * 100.0 / _timeoutCount:F1}%)");
            }

            Console.WriteLine();
            Console.WriteLine("关键指标:");
            Console.WriteLine($"  队列峰值深度: {_peakQueueDepth}");
            Console.WriteLine($"  最长队列等待: {_maxQueueWaitTimeMs:F0}ms");

            // 计算延迟统计
            var successResults = _allResults.Where(r => r.Success).ToList();
            if (successResults.Count > 0)
            {
                var latencies = successResults.Select(r => r.TotalTimeMs).OrderBy(x => x).ToList();
                Console.WriteLine();
                Console.WriteLine("成功请求延迟分布:");
                Console.WriteLine($"  最小: {latencies.First():F1}ms");
                Console.WriteLine($"  P50: {latencies[(int)(latencies.Count * 0.5)]:F1}ms");
                Console.WriteLine($"  P95: {latencies[(int)(latencies.Count * 0.95)]:F1}ms");
                Console.WriteLine($"  P99: {latencies[(int)(latencies.Count * 0.99)]:F1}ms");
                Console.WriteLine($"  最大: {latencies.Last():F1}ms");
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("==============================");
            Console.ResetColor();
        }
    }
}
