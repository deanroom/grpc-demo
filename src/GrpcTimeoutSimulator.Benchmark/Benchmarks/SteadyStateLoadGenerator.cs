using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcTimeoutSimulator.Proto;

namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

/// <summary>
/// 超时原因枚举
/// </summary>
[Flags]
public enum TimeoutReason
{
    None = 0,
    /// <summary>
    /// 请求未到达服务端（HTTP/2 连接层排队超时）
    /// </summary>
    Http2ConnectionLayer = 1,
    /// <summary>
    /// 服务端队列等待过长
    /// </summary>
    ServerQueueWait = 2,
    /// <summary>
    /// 服务端处理时间过长
    /// </summary>
    ServerProcessing = 4,
    /// <summary>
    /// 客户端被取消
    /// </summary>
    ClientCancelled = 8
}

/// <summary>
/// 稳态负载生成器
/// 使用 SemaphoreSlim 控制并发，持续发送请求
/// </summary>
public class SteadyStateLoadGenerator : IDisposable
{
    private readonly string _serverAddress;
    private readonly ClientConfig _clientConfig;
    private readonly int _requestTimeoutMs;
    private GrpcChannel[] _channels = null!;
    private SimulationService.SimulationServiceClient[] _clients = null!;
    private int _channelIndex;
    private int _requestIdCounter;

    public SteadyStateLoadGenerator(string serverAddress, ClientConfig clientConfig, int requestTimeoutMs)
    {
        _serverAddress = serverAddress;
        _clientConfig = clientConfig;
        _requestTimeoutMs = requestTimeoutMs;
        InitializeChannels();
    }

    private void InitializeChannels()
    {
        _channels = new GrpcChannel[_clientConfig.ChannelPoolSize];
        _clients = new SimulationService.SimulationServiceClient[_clientConfig.ChannelPoolSize];

        for (int i = 0; i < _clientConfig.ChannelPoolSize; i++)
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = _clientConfig.EnableMultipleHttp2Connections,
                MaxConnectionsPerServer = _clientConfig.MaxConnectionsPerServer,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                InitialHttp2StreamWindowSize = 1024 * 1024,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };

            _channels[i] = GrpcChannel.ForAddress(_serverAddress, new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 16 * 1024 * 1024,
                MaxSendMessageSize = 16 * 1024 * 1024,
            });
            _clients[i] = new SimulationService.SimulationServiceClient(_channels[i]);
        }
    }

    /// <summary>
    /// 重新初始化连接（用于配置优化测试）
    /// </summary>
    public void ReinitializeChannels(ClientConfig newConfig)
    {
        // 先释放现有连接
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        // 使用新配置重新初始化
        _channels = new GrpcChannel[newConfig.ChannelPoolSize];
        _clients = new SimulationService.SimulationServiceClient[newConfig.ChannelPoolSize];

        for (int i = 0; i < newConfig.ChannelPoolSize; i++)
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = newConfig.EnableMultipleHttp2Connections,
                MaxConnectionsPerServer = newConfig.MaxConnectionsPerServer,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                InitialHttp2StreamWindowSize = 1024 * 1024,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };

            _channels[i] = GrpcChannel.ForAddress(_serverAddress, new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 16 * 1024 * 1024,
                MaxSendMessageSize = 16 * 1024 * 1024,
            });
            _clients[i] = new SimulationService.SimulationServiceClient(_channels[i]);
        }
    }

    private SimulationService.SimulationServiceClient GetNextClient()
    {
        int index = Interlocked.Increment(ref _channelIndex) % _channels.Length;
        return _clients[index];
    }

    /// <summary>
    /// 运行稳态负载测试
    /// </summary>
    /// <param name="concurrency">并发数</param>
    /// <param name="durationSec">测试时长（秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>测试结果</returns>
    public async Task<LoadTestResult> RunAsync(int concurrency, int durationSec, CancellationToken cancellationToken = default)
    {
        var result = new LoadTestResult();
        var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var stopwatch = Stopwatch.StartNew();
        var testDuration = TimeSpan.FromSeconds(durationSec);
        var tasks = new List<Task>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 启动请求循环
        while (stopwatch.Elapsed < testDuration && !cts.Token.IsCancellationRequested)
        {
            await semaphore.WaitAsync(cts.Token);

            var task = Task.Run(async () =>
            {
                try
                {
                    var requestResult = await SendRequestAsync(cts.Token);
                    lock (result)
                    {
                        result.TotalRequests++;
                        if (requestResult.Success)
                        {
                            result.SuccessCount++;
                            result.Latencies.Add(requestResult.LatencyMs);

                            // 记录服务端时间分解
                            if (requestResult.QueueWaitMs > 0)
                            {
                                result.ServerQueueWaitTimes.Add(requestResult.QueueWaitMs);
                            }
                        }
                        else if (requestResult.IsTimeout)
                        {
                            result.TimeoutCount++;

                            // 记录超时原因
                            if (requestResult.TimeoutReason != TimeoutReason.None)
                            {
                                result.TimeoutReasons.Add(requestResult.TimeoutReason);
                            }
                        }
                        else
                        {
                            result.ErrorCount++;
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cts.Token);

            tasks.Add(task);

            // 清理已完成的任务以避免内存累积
            if (tasks.Count > concurrency * 10)
            {
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        // 等待所有进行中的请求完成
        try
        {
            await Task.WhenAll(tasks.Where(t => !t.IsCompleted));
        }
        catch (OperationCanceledException)
        {
            // 忽略取消异常
        }

        stopwatch.Stop();
        result.DurationSec = stopwatch.Elapsed.TotalSeconds;

        return result;
    }

    private async Task<RequestResult> SendRequestAsync(CancellationToken cancellationToken)
    {
        var requestId = $"BENCH-{Interlocked.Increment(ref _requestIdCounter):D6}";
        var sendTimeTicks = DateTime.UtcNow.Ticks;

        try
        {
            var request = new ProcessRequest
            {
                RequestId = requestId,
                ClientSendTimeTicks = sendTimeTicks
            };

            var deadline = DateTime.UtcNow.AddMilliseconds(_requestTimeoutMs);
            var callOptions = new CallOptions(deadline: deadline, cancellationToken: cancellationToken);
            var client = GetNextClient();

            var response = await client.ProcessAsync(request, callOptions);

            var receiveTimeTicks = DateTime.UtcNow.Ticks;
            var latencyMs = (receiveTimeTicks - sendTimeTicks) / (double)TimeSpan.TicksPerMillisecond;

            // 计算服务端队列等待时间
            double queueWaitMs = 0;
            if (response.Timeline != null && response.Timeline.DequeueTimeTicks > 0 && response.Timeline.EnqueueTimeTicks > 0)
            {
                queueWaitMs = (response.Timeline.DequeueTimeTicks - response.Timeline.EnqueueTimeTicks)
                    / (double)TimeSpan.TicksPerMillisecond;
            }

            return new RequestResult
            {
                Success = response.Success,
                LatencyMs = latencyMs,
                QueueWaitMs = queueWaitMs,
                HasServerTimeline = response.Timeline != null
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            // 超时：判断是客户端 HTTP/2 层还是服务端应用层
            // DeadlineExceeded 且没有从服务端收到响应 → HTTP/2 连接层问题
            // 注意：无法直接获取部分响应，所以 DeadlineExceeded 通常意味着请求未完成
            return new RequestResult
            {
                IsTimeout = true,
                TimeoutReason = TimeoutReason.Http2ConnectionLayer  // 假设是连接层问题
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // 被取消：可能是服务端处理超时后取消
            return new RequestResult
            {
                IsTimeout = true,
                TimeoutReason = TimeoutReason.ClientCancelled
            };
        }
        catch (RpcException)
        {
            return new RequestResult { IsError = true };
        }
        catch (OperationCanceledException)
        {
            return new RequestResult
            {
                IsError = true,
                TimeoutReason = TimeoutReason.ClientCancelled
            };
        }
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private struct RequestResult
    {
        public bool Success;
        public bool IsTimeout;
        public bool IsError;
        public double LatencyMs;
        public double QueueWaitMs;
        public bool HasServerTimeline;
        public TimeoutReason TimeoutReason;
    }
}

/// <summary>
/// 负载测试结果
/// </summary>
public class LoadTestResult
{
    public int TotalRequests { get; set; }
    public int SuccessCount { get; set; }
    public int TimeoutCount { get; set; }
    public int ErrorCount { get; set; }
    public double DurationSec { get; set; }
    public List<double> Latencies { get; set; } = [];

    /// <summary>
    /// 服务端队列等待时间列表（成功请求）
    /// </summary>
    public List<double> ServerQueueWaitTimes { get; set; } = [];

    /// <summary>
    /// 超时原因列表
    /// </summary>
    public List<TimeoutReason> TimeoutReasons { get; set; } = [];

    public double SuccessRate => TotalRequests > 0 ? (double)SuccessCount / TotalRequests : 0;
    public double Throughput => DurationSec > 0 ? SuccessCount / DurationSec : 0;

    /// <summary>
    /// HTTP/2 连接层超时数量
    /// </summary>
    public int Http2LayerTimeoutCount => TimeoutReasons.Count(r => r.HasFlag(TimeoutReason.Http2ConnectionLayer));

    /// <summary>
    /// 服务端应用层超时数量
    /// </summary>
    public int ServerLayerTimeoutCount => TimeoutReasons.Count(r =>
        r.HasFlag(TimeoutReason.ServerQueueWait) || r.HasFlag(TimeoutReason.ServerProcessing));

    /// <summary>
    /// 平均队列等待时间
    /// </summary>
    public double AvgQueueWaitMs => ServerQueueWaitTimes.Count > 0 ? ServerQueueWaitTimes.Average() : 0;

    /// <summary>
    /// P99 队列等待时间
    /// </summary>
    public double P99QueueWaitMs
    {
        get
        {
            if (ServerQueueWaitTimes.Count == 0) return 0;
            var sorted = ServerQueueWaitTimes.OrderBy(x => x).ToList();
            var index = (int)Math.Ceiling(0.99 * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
        }
    }
}
