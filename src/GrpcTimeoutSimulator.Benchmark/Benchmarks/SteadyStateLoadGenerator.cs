using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcTimeoutSimulator.Proto;

namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

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
                        }
                        else if (requestResult.IsTimeout)
                        {
                            result.TimeoutCount++;
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

            return new RequestResult
            {
                Success = response.Success,
                LatencyMs = latencyMs
            };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            return new RequestResult { IsTimeout = true };
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            return new RequestResult { IsTimeout = true };
        }
        catch (RpcException)
        {
            return new RequestResult { IsError = true };
        }
        catch (OperationCanceledException)
        {
            return new RequestResult { IsError = true };
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

    public double SuccessRate => TotalRequests > 0 ? (double)SuccessCount / TotalRequests : 0;
    public double Throughput => DurationSec > 0 ? SuccessCount / DurationSec : 0;
}
