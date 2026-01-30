using System.Collections.Concurrent;

namespace GrpcTimeoutSimulator.Server.Diagnostics;

/// <summary>
/// 请求时间线，记录请求各阶段时间戳
/// </summary>
public class RequestTimeline
{
    public string RequestId { get; set; } = string.Empty;

    // T1: 客户端发起时间 (从请求中获取)
    public long ClientSendTimeTicks { get; set; }

    // T2: 请求到达服务端时间
    public long ArrivalTimeTicks { get; set; }

    // T3: 进入队列时间
    public long EnqueueTimeTicks { get; set; }

    // T4: 从队列取出开始处理时间
    public long DequeueTimeTicks { get; set; }

    // T5: 处理完成时间
    public long CompleteTimeTicks { get; set; }

    // 队列深度（入队时）
    public int QueueDepthAtEnqueue { get; set; }

    // GC 相关
    public bool GcOccurred { get; set; }
    public int GcGeneration { get; set; }
    public long GcDurationMs { get; set; }

    // 线程池状态（入队时）
    public int AvailableWorkerThreads { get; set; }
    public int AvailableIoThreads { get; set; }

    // 实际处理时长 (us)
    public long ProcessingTimeUs { get; set; }

    /// <summary>
    /// 计算队列等待时间 (ms)
    /// </summary>
    public double QueueWaitTimeMs => (DequeueTimeTicks - EnqueueTimeTicks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 计算处理时间 (ms)
    /// </summary>
    public double ProcessingTimeMs => (CompleteTimeTicks - DequeueTimeTicks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 计算总服务端时间 (ms)
    /// </summary>
    public double TotalServerTimeMs => (CompleteTimeTicks - ArrivalTimeTicks) / (double)TimeSpan.TicksPerMillisecond;
}

/// <summary>
/// GC 事件信息
/// </summary>
public record GcEvent(DateTime Timestamp, int Generation, long DurationMs);

/// <summary>
/// 超时诊断系统
/// </summary>
public class TimeoutDiagnostics : IDisposable
{
    private readonly ConcurrentQueue<GcEvent> _gcEvents = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _gcMonitorTask;
    private volatile int _minAvailableWorkerThreads = int.MaxValue;
    private volatile int _minAvailableIoThreads = int.MaxValue;
    private int _gcGen0Count;
    private int _gcGen1Count;
    private int _gcGen2Count;
    private long _lastGcTimeTicks;

    public TimeoutDiagnostics()
    {
        _gcGen0Count = GC.CollectionCount(0);
        _gcGen1Count = GC.CollectionCount(1);
        _gcGen2Count = GC.CollectionCount(2);
        _lastGcTimeTicks = DateTime.UtcNow.Ticks;

        _gcMonitorTask = Task.Run(MonitorGcAsync);
    }

    /// <summary>
    /// 记录当前线程池状态
    /// </summary>
    public (int workerThreads, int ioThreads) RecordThreadPoolState()
    {
        ThreadPool.GetAvailableThreads(out int workerThreads, out int ioThreads);

        // 更新最小值
        if (workerThreads < _minAvailableWorkerThreads)
            _minAvailableWorkerThreads = workerThreads;
        if (ioThreads < _minAvailableIoThreads)
            _minAvailableIoThreads = ioThreads;

        return (workerThreads, ioThreads);
    }

    /// <summary>
    /// 检查指定时间段内是否发生 GC
    /// </summary>
    public (bool occurred, int generation, long durationMs) CheckGcDuring(long startTicks, long endTicks)
    {
        foreach (var gcEvent in _gcEvents)
        {
            var eventTicks = gcEvent.Timestamp.Ticks;
            if (eventTicks >= startTicks && eventTicks <= endTicks)
            {
                return (true, gcEvent.Generation, gcEvent.DurationMs);
            }
        }
        return (false, 0, 0);
    }

    /// <summary>
    /// 获取 GC 统计信息
    /// </summary>
    public (int gen0, int gen1, int gen2) GetGcCounts()
    {
        return (
            GC.CollectionCount(0) - _gcGen0Count,
            GC.CollectionCount(1) - _gcGen1Count,
            GC.CollectionCount(2) - _gcGen2Count
        );
    }

    /// <summary>
    /// 获取线程池最低可用线程数
    /// </summary>
    public (int minWorker, int minIo) GetMinThreadPoolStats()
    {
        return (_minAvailableWorkerThreads == int.MaxValue ? 0 : _minAvailableWorkerThreads,
                _minAvailableIoThreads == int.MaxValue ? 0 : _minAvailableIoThreads);
    }

    /// <summary>
    /// 重置统计
    /// </summary>
    public void Reset()
    {
        _gcGen0Count = GC.CollectionCount(0);
        _gcGen1Count = GC.CollectionCount(1);
        _gcGen2Count = GC.CollectionCount(2);
        _minAvailableWorkerThreads = int.MaxValue;
        _minAvailableIoThreads = int.MaxValue;

        // 清空 GC 事件队列
        while (_gcEvents.TryDequeue(out _)) { }
    }

    private async Task MonitorGcAsync()
    {
        int lastGen0 = GC.CollectionCount(0);
        int lastGen1 = GC.CollectionCount(1);
        int lastGen2 = GC.CollectionCount(2);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, _cts.Token); // 每 10ms 检查一次

                int currentGen0 = GC.CollectionCount(0);
                int currentGen1 = GC.CollectionCount(1);
                int currentGen2 = GC.CollectionCount(2);

                var now = DateTime.UtcNow;
                long currentTicks = now.Ticks;
                long estimatedDuration = (currentTicks - _lastGcTimeTicks) / TimeSpan.TicksPerMillisecond;

                if (currentGen2 > lastGen2)
                {
                    _gcEvents.Enqueue(new GcEvent(now, 2, Math.Min(estimatedDuration, 500)));
                    lastGen2 = currentGen2;
                }
                else if (currentGen1 > lastGen1)
                {
                    _gcEvents.Enqueue(new GcEvent(now, 1, Math.Min(estimatedDuration, 100)));
                    lastGen1 = currentGen1;
                }
                else if (currentGen0 > lastGen0)
                {
                    _gcEvents.Enqueue(new GcEvent(now, 0, Math.Min(estimatedDuration, 10)));
                    lastGen0 = currentGen0;
                }

                _lastGcTimeTicks = currentTicks;

                // 保持队列大小合理（最近 1000 个事件）
                while (_gcEvents.Count > 1000)
                {
                    _gcEvents.TryDequeue(out _);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _gcMonitorTask.Wait(1000);
        }
        catch { }
        _cts.Dispose();
    }
}
