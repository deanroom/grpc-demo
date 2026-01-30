using GrpcTimeoutSimulator.Server.Diagnostics;
using GrpcTimeoutSimulator.Server.Processing;
using GrpcTimeoutSimulator.Server.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// 优化线程池（在高并发场景下很重要）
// 设置最小线程数，避免线程池饥饿
ThreadPool.SetMinThreads(workerThreads: 200, completionPortThreads: 200);

var builder = WebApplication.CreateBuilder(args);

// 配置 Kestrel 支持高并发 HTTP/2
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5000, o => o.Protocols = HttpProtocols.Http2);

    // HTTP/2 限制配置（全局）
    options.Limits.Http2.MaxStreamsPerConnection = 1000;           // 默认 100
    options.Limits.Http2.InitialConnectionWindowSize = 1024 * 1024; // 1MB
    options.Limits.Http2.InitialStreamWindowSize = 512 * 1024;      // 512KB

    // 全局连接限制
    options.Limits.MaxConcurrentConnections = 10000;
    options.Limits.MaxConcurrentUpgradedConnections = 10000;
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
builder.Services.AddGrpc();

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
