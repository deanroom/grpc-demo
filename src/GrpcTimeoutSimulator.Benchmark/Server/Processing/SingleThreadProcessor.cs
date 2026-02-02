using System.Collections.Concurrent;
using System.Diagnostics;

namespace GrpcTimeoutSimulator.Benchmark.Server.Processing;

/// <summary>
/// 请求时间线
/// </summary>
public class RequestTimeline
{
    public string RequestId { get; set; } = "";
    public long ClientSendTimeTicks { get; set; }
    public long ArrivalTimeTicks { get; set; }
    public long EnqueueTimeTicks { get; set; }
    public long DequeueTimeTicks { get; set; }
    public long CompleteTimeTicks { get; set; }
    public int QueueDepthAtEnqueue { get; set; }
}

/// <summary>
/// 工作项
/// </summary>
public class WorkItem
{
    public required RequestTimeline Timeline { get; init; }
    public required TaskCompletionSource<bool> CompletionSource { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// 单线程处理器配置
/// </summary>
public class ProcessorConfig
{
    /// <summary>
    /// 最小处理时间（微秒）
    /// </summary>
    public int MinProcessingTimeUs { get; set; } = 10;

    /// <summary>
    /// 最大处理时间（毫秒）
    /// </summary>
    public int MaxProcessingTimeMs { get; set; } = 50;
}

/// <summary>
/// 单线程处理器，使用 BlockingCollection 实现
/// </summary>
public class SingleThreadProcessor : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _processingThread;
    private readonly ProcessorConfig _config;
    private readonly Random _random = new();
    private int _peakQueueDepth;
    private long _maxQueueWaitTicks;
    private int _cancelledCount;
    private int _processedCount;

    public SingleThreadProcessor(ProcessorConfig config)
    {
        _config = config;
        _processingThread = new Thread(ProcessQueue)
        {
            Name = "SingleThreadProcessor",
            IsBackground = true
        };
        _processingThread.Start();
    }

    /// <summary>
    /// 当前队列深度
    /// </summary>
    public int QueueDepth => _queue.Count;

    /// <summary>
    /// 峰值队列深度
    /// </summary>
    public int PeakQueueDepth => Interlocked.CompareExchange(ref _peakQueueDepth, 0, 0);

    /// <summary>
    /// 最长队列等待时间 (ms)
    /// </summary>
    public double MaxQueueWaitTimeMs => Interlocked.Read(ref _maxQueueWaitTicks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// 已处理请求数
    /// </summary>
    public int ProcessedCount => Interlocked.CompareExchange(ref _processedCount, 0, 0);

    /// <summary>
    /// 已取消请求数（在队列中超时）
    /// </summary>
    public int CancelledCount => Interlocked.CompareExchange(ref _cancelledCount, 0, 0);

    /// <summary>
    /// 入队请求
    /// </summary>
    public void Enqueue(WorkItem item)
    {
        item.Timeline.EnqueueTimeTicks = DateTime.UtcNow.Ticks;

        // 记录队列深度
        int depth = _queue.Count;
        item.Timeline.QueueDepthAtEnqueue = depth;

        // 更新峰值（使用 Interlocked 进行线程安全更新）
        int currentPeak;
        while (depth > (currentPeak = Interlocked.CompareExchange(ref _peakQueueDepth, 0, 0)))
        {
            if (Interlocked.CompareExchange(ref _peakQueueDepth, depth, currentPeak) == currentPeak)
                break;
        }

        _queue.Add(item);
    }

    /// <summary>
    /// 重置统计
    /// </summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _peakQueueDepth, 0);
        Interlocked.Exchange(ref _maxQueueWaitTicks, 0);
        Interlocked.Exchange(ref _processedCount, 0);
        Interlocked.Exchange(ref _cancelledCount, 0);
    }

    private void ProcessQueue()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                // 检查是否已取消
                if (item.CancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _cancelledCount);
                    item.CompletionSource.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                // T4: 记录出队时间
                item.Timeline.DequeueTimeTicks = DateTime.UtcNow.Ticks;

                // 更新最长队列等待时间（使用 Interlocked 进行线程安全更新）
                long waitTicks = item.Timeline.DequeueTimeTicks - item.Timeline.EnqueueTimeTicks;
                long currentMax;
                while (waitTicks > (currentMax = Interlocked.Read(ref _maxQueueWaitTicks)))
                {
                    if (Interlocked.CompareExchange(ref _maxQueueWaitTicks, waitTicks, currentMax) == currentMax)
                        break;
                }

                // 模拟处理
                SimulateProcessing();

                // T5: 记录完成时间
                item.Timeline.CompleteTimeTicks = DateTime.UtcNow.Ticks;

                Interlocked.Increment(ref _processedCount);

                item.CompletionSource.TrySetResult(true);
            }
            catch (Exception ex)
            {
                item.CompletionSource.TrySetException(ex);
            }
        }
    }

    private void SimulateProcessing()
    {
        // 随机选择处理时间：从微秒级到毫秒级
        // 使用对数分布，让短时间更常见
        double ratio = _random.NextDouble();
        double logMin = Math.Log(_config.MinProcessingTimeUs);
        double logMax = Math.Log(_config.MaxProcessingTimeMs * 1000); // 转换为微秒
        double logValue = logMin + ratio * (logMax - logMin);
        int delayUs = (int)Math.Exp(logValue);

        // 精确延时
        if (delayUs < 1000) // 小于 1ms 使用 SpinWait
        {
            SpinWait(delayUs);
        }
        else // 大于 1ms 使用 Thread.Sleep + SpinWait
        {
            int sleepMs = delayUs / 1000;
            int remainUs = delayUs % 1000;
            if (sleepMs > 0)
                Thread.Sleep(sleepMs);
            if (remainUs > 0)
                SpinWait(remainUs);
        }
    }

    private static void SpinWait(int microseconds)
    {
        long targetTicks = Stopwatch.GetTimestamp() + (microseconds * Stopwatch.Frequency / 1_000_000);
        while (Stopwatch.GetTimestamp() < targetTicks)
        {
            Thread.SpinWait(10);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _processingThread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
