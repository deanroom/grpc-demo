using Grpc.Core;
using Grpc.Net.Client;
using GrpcTimeoutSimulator.Proto;
using GrpcTimeoutSimulator.Client.Diagnostics;

namespace GrpcTimeoutSimulator.Client.LoadGenerators;

/// <summary>
/// 负载配置
/// </summary>
public class LoadConfig
{
    /// <summary>
    /// 每次突发的请求数
    /// </summary>
    public int BurstSize { get; set; } = 100;

    /// <summary>
    /// 突发内请求间隔（ms）
    /// </summary>
    public int BurstIntervalMs { get; set; } = 1;

    /// <summary>
    /// 突发次数
    /// </summary>
    public int BurstCount { get; set; } = 10;

    /// <summary>
    /// 突发之间的间隔（ms）
    /// </summary>
    public int BurstGapMs { get; set; } = 500;

    /// <summary>
    /// 超时时间（ms）
    /// </summary>
    public int DeadlineMs { get; set; } = 3000;

    /// <summary>
    /// 是否使用同步调用
    /// </summary>
    public bool UseSyncCalls { get; set; } = true;

    /// <summary>
    /// 服务端地址
    /// </summary>
    public string ServerAddress { get; set; } = "http://localhost:5000";
}

/// <summary>
/// 请求结果
/// </summary>
public class RequestResult
{
    public required string RequestId { get; init; }
    public bool Success { get; set; }
    public bool IsTimeout { get; set; }
    public string? ErrorMessage { get; set; }

    // 客户端时间点
    public long ClientSendTimeTicks { get; set; }
    public long ClientReceiveTimeTicks { get; set; }

    // 服务端时间线（成功时填充）
    public ServerTimeline? ServerTimeline { get; set; }
    public DiagnosticInfo? DiagnosticInfo { get; set; }
    public int QueueDepthAtEnqueue { get; set; }

    /// <summary>
    /// 总耗时 (ms)
    /// </summary>
    public double TotalTimeMs => (ClientReceiveTimeTicks - ClientSendTimeTicks) / (double)TimeSpan.TicksPerMillisecond;
}

/// <summary>
/// 突发负载生成器
/// </summary>
public class BurstLoadGenerator
{
    private readonly LoadConfig _config;
    private readonly ClientDiagnostics _diagnostics;
    private readonly GrpcChannel[] _channels;
    private readonly SimulationService.SimulationServiceClient[] _clients;
    private int _requestIdCounter;
    private int _channelIndex;

    // 连接池大小（多个 Channel 可以进一步提升并发）
    private const int ChannelPoolSize = 4;

    public BurstLoadGenerator(LoadConfig config, ClientDiagnostics diagnostics)
    {
        _config = config;
        _diagnostics = diagnostics;

        // 创建连接池
        _channels = new GrpcChannel[ChannelPoolSize];
        _clients = new SimulationService.SimulationServiceClient[ChannelPoolSize];

        for (int i = 0; i < ChannelPoolSize; i++)
        {
            // 配置 HTTP handler 以支持高并发
            var handler = new SocketsHttpHandler
            {
                // 启用多 HTTP/2 连接
                EnableMultipleHttp2Connections = true,

                // 连接池设置
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),

                // 增加连接限制
                MaxConnectionsPerServer = 100,
            };

            _channels[i] = GrpcChannel.ForAddress(_config.ServerAddress, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
            _clients[i] = new SimulationService.SimulationServiceClient(_channels[i]);
        }
    }

    /// <summary>
    /// 轮询获取下一个客户端（负载均衡）
    /// </summary>
    private SimulationService.SimulationServiceClient GetNextClient()
    {
        int index = Interlocked.Increment(ref _channelIndex) % ChannelPoolSize;
        return _clients[index];
    }

    /// <summary>
    /// 运行负载测试
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"开始负载测试:");
        Console.WriteLine($"  突发大小: {_config.BurstSize}");
        Console.WriteLine($"  突发次数: {_config.BurstCount}");
        Console.WriteLine($"  突发间隔: {_config.BurstGapMs}ms");
        Console.WriteLine($"  超时时间: {_config.DeadlineMs}ms");
        Console.WriteLine($"  调用模式: {(_config.UseSyncCalls ? "同步" : "异步")}");
        Console.WriteLine();

        for (int burst = 0; burst < _config.BurstCount && !cancellationToken.IsCancellationRequested; burst++)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 发起第 {burst + 1}/{_config.BurstCount} 轮突发 ({_config.BurstSize} 请求)...");

            var tasks = new List<Task>();
            for (int i = 0; i < _config.BurstSize; i++)
            {
                var requestId = $"REQ-{Interlocked.Increment(ref _requestIdCounter):D5}";
                var task = SendRequestAsync(requestId);
                tasks.Add(task);

                // 突发内间隔
                if (_config.BurstIntervalMs > 0 && i < _config.BurstSize - 1)
                {
                    await Task.Delay(_config.BurstIntervalMs, cancellationToken);
                }
            }

            // 等待本轮所有请求完成
            await Task.WhenAll(tasks);

            // 打印本轮统计
            _diagnostics.PrintBurstSummary(burst + 1);

            // 突发间隔
            if (burst < _config.BurstCount - 1)
            {
                await Task.Delay(_config.BurstGapMs, cancellationToken);
            }
        }
    }

    private async Task SendRequestAsync(string requestId)
    {
        var result = new RequestResult { RequestId = requestId };

        try
        {
            // T1: 记录发起时间
            result.ClientSendTimeTicks = DateTime.UtcNow.Ticks;

            var request = new ProcessRequest
            {
                RequestId = requestId,
                ClientSendTimeTicks = result.ClientSendTimeTicks
            };

            var deadline = DateTime.UtcNow.AddMilliseconds(_config.DeadlineMs);
            var callOptions = new CallOptions(deadline: deadline);

            // 从连接池获取客户端（负载均衡）
            var client = GetNextClient();

            ProcessResponse response;
            if (_config.UseSyncCalls)
            {
                // 同步调用（在后台线程运行）
                response = await Task.Run(() => client.Process(request, callOptions));
            }
            else
            {
                // 异步调用
                response = await client.ProcessAsync(request, callOptions);
            }

            // T6: 记录接收时间
            result.ClientReceiveTimeTicks = DateTime.UtcNow.Ticks;
            result.Success = response.Success;
            result.ServerTimeline = response.Timeline;
            result.DiagnosticInfo = response.DiagnosticInfo;
            result.QueueDepthAtEnqueue = response.QueueDepthAtEnqueue;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            result.ClientReceiveTimeTicks = DateTime.UtcNow.Ticks;
            result.IsTimeout = true;
            result.ErrorMessage = "Deadline Exceeded";
        }
        catch (RpcException ex)
        {
            result.ClientReceiveTimeTicks = DateTime.UtcNow.Ticks;
            result.ErrorMessage = $"RPC Error: {ex.StatusCode} - {ex.Message}";
        }
        catch (Exception ex)
        {
            result.ClientReceiveTimeTicks = DateTime.UtcNow.Ticks;
            result.ErrorMessage = $"Error: {ex.Message}";
        }

        _diagnostics.RecordResult(result);
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }
}
