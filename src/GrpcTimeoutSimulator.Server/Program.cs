using GrpcTimeoutSimulator.Server.Diagnostics;
using GrpcTimeoutSimulator.Server.Processing;
using GrpcTimeoutSimulator.Server.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// 优化线程池（在高并发场景下很重要）
// 设置最小线程数，避免线程池饥饿
ThreadPool.SetMinThreads(workerThreads: 200, completionPortThreads: 200);

var builder = WebApplication.CreateBuilder(args);

// 配置 Kestrel 支持高并发 HTTP/2（Unary + Server Streaming 混合场景）
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000, o => o.Protocols = HttpProtocols.Http2);

    // ============================================================
    // HTTP/2 并发流配置
    // ============================================================

    // 每连接最大并发流数（默认 100）
    // - Unary RPC: 流快速释放，可设置较高值
    // - Server Streaming: 流长时间占用，需要权衡
    // 混合场景建议：根据 Streaming 流的预期数量 + Unary 并发数来设置
    options.Limits.Http2.MaxStreamsPerConnection = 500;

    // ============================================================
    // 流量控制窗口配置（对 Server Streaming 尤为重要）
    // ============================================================

    // 连接级窗口大小（所有流共享，默认 128KB）
    // Server Streaming 场景需要更大的窗口避免频繁的 WINDOW_UPDATE
    options.Limits.Http2.InitialConnectionWindowSize = 2 * 1024 * 1024; // 2MB

    // 流级窗口大小（每个流独立，默认 96KB）
    // 对于高吞吐的 Server Streaming，建议增大
    options.Limits.Http2.InitialStreamWindowSize = 1024 * 1024; // 1MB

    // ============================================================
    // Keep-Alive 配置（长时间流的连接保活）
    // ============================================================

    // Keep-Alive 间隔（默认无限）
    // Server Streaming 长连接需要定期 ping 检测连接健康
    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);

    // Keep-Alive 超时（默认 20 秒）
    options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);

    // ============================================================
    // 全局连接限制
    // ============================================================
    options.Limits.MaxConcurrentConnections = 10000;
    options.Limits.MaxConcurrentUpgradedConnections = 10000;

    // 请求体大小限制（如果 Streaming 请求携带大数据）
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100MB
});

// 配置处理器参数（可从命令行或配置文件读取）
var processorConfig = new ProcessorConfig
{
    MinProcessingTimeUs = builder.Configuration.GetValue("MinProcessingTimeUs", 10),
    MaxProcessingTimeMs = builder.Configuration.GetValue("MaxProcessingTimeMs", 50)
};

// 注册服务
builder.Services.AddSingleton(processorConfig);
builder.Services.AddSingleton<TimeoutDiagnostics>();
builder.Services.AddSingleton<SingleThreadProcessor>();

// gRPC 服务配置（针对 Unary + Server Streaming 混合场景）
builder.Services.AddGrpc(options =>
{
    // ============================================================
    // 消息大小限制
    // ============================================================

    // 最大接收消息大小（默认 4MB）
    options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16MB

    // 最大发送消息大小（默认无限制）
    // Server Streaming 场景下，单条消息不宜过大，建议分批发送
    options.MaxSendMessageSize = 16 * 1024 * 1024; // 16MB

    // ============================================================
    // 压缩配置（减少网络传输，提升 Streaming 吞吐）
    // ============================================================

    // 响应压缩级别
    options.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Fastest;

    // 响应压缩算法
    options.ResponseCompressionAlgorithm = "gzip";

    // ============================================================
    // 拦截器（可用于监控 Streaming 状态）
    // ============================================================
    // options.Interceptors.Add<StreamingMonitorInterceptor>();
});

var app = builder.Build();

// 配置 gRPC 服务
app.MapGrpcService<SimulationService>();

// 健康检查端点（注意：HTTP/2 only 模式下浏览器可能无法访问）
app.MapGet("/", () => "gRPC Timeout Simulator Server is running");
app.MapGet("/stats", (SingleThreadProcessor processor, TimeoutDiagnostics diagnostics) =>
{
    var gcCounts = diagnostics.GetGcCounts();
    var threadPoolStats = diagnostics.GetMinThreadPoolStats();

    return new
    {
        QueueDepth = processor.QueueDepth,
        PeakQueueDepth = processor.PeakQueueDepth,
        MaxQueueWaitTimeMs = processor.MaxQueueWaitTimeMs,
        ProcessedCount = processor.ProcessedCount,
        CancelledCount = processor.CancelledCount,
        GcCounts = new { Gen0 = gcCounts.gen0, Gen1 = gcCounts.gen1, Gen2 = gcCounts.gen2 },
        MinThreadPool = new { Worker = threadPoolStats.minWorker, IO = threadPoolStats.minIo }
    };
});

Console.WriteLine("gRPC Timeout Simulator Server starting...");
Console.WriteLine($"Listening on: http://localhost:5000 (HTTP/2)");
Console.WriteLine($"Processing time range: {processorConfig.MinProcessingTimeUs}us - {processorConfig.MaxProcessingTimeMs}ms");

app.Run();
