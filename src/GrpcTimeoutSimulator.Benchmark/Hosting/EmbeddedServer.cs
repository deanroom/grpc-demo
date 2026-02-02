using GrpcTimeoutSimulator.Server.Diagnostics;
using GrpcTimeoutSimulator.Server.Processing;
using GrpcTimeoutSimulator.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrpcTimeoutSimulator.Benchmark.Hosting;

/// <summary>
/// 内嵌服务端启动器，在进程内启动 Kestrel + gRPC 服务
/// </summary>
public class EmbeddedServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly int _port;
    private readonly SingleThreadProcessor _processor;
    private readonly TimeoutDiagnostics _diagnostics;

    public string ServerAddress => $"http://localhost:{_port}";
    public SingleThreadProcessor Processor => _processor;
    public TimeoutDiagnostics Diagnostics => _diagnostics;

    private EmbeddedServer(WebApplication app, int port, SingleThreadProcessor processor, TimeoutDiagnostics diagnostics)
    {
        _app = app;
        _port = port;
        _processor = processor;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 创建并启动内嵌服务端
    /// </summary>
    public static async Task<EmbeddedServer> StartAsync(EmbeddedServerOptions options)
    {
        // 优化线程池
        ThreadPool.SetMinThreads(
            workerThreads: options.MinWorkerThreads,
            completionPortThreads: options.MinIoThreads);

        var builder = WebApplication.CreateBuilder();

        // 禁用默认日志输出以保持控制台整洁
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // 配置 Kestrel
        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            kestrelOptions.ListenLocalhost(options.Port, o => o.Protocols = HttpProtocols.Http2);
            kestrelOptions.Limits.Http2.MaxStreamsPerConnection = options.MaxStreamsPerConnection;
            kestrelOptions.Limits.Http2.InitialConnectionWindowSize = 2 * 1024 * 1024;
            kestrelOptions.Limits.Http2.InitialStreamWindowSize = 1024 * 1024;
            kestrelOptions.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
            kestrelOptions.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);
            kestrelOptions.Limits.MaxConcurrentConnections = 10000;
            kestrelOptions.Limits.MaxConcurrentUpgradedConnections = 10000;
        });

        // 配置处理器参数
        var processorConfig = new ProcessorConfig
        {
            MinProcessingTimeUs = options.MinProcessingTimeUs,
            MaxProcessingTimeMs = options.MaxProcessingTimeMs
        };

        // 注册服务
        builder.Services.AddSingleton(processorConfig);
        builder.Services.AddSingleton<TimeoutDiagnostics>();
        builder.Services.AddSingleton<SingleThreadProcessor>();
        builder.Services.AddGrpc(grpcOptions =>
        {
            grpcOptions.MaxReceiveMessageSize = 16 * 1024 * 1024;
            grpcOptions.MaxSendMessageSize = 16 * 1024 * 1024;
        });

        var app = builder.Build();
        app.MapGrpcService<SimulationService>();

        // 获取服务实例以便外部访问
        var processor = app.Services.GetRequiredService<SingleThreadProcessor>();
        var diagnostics = app.Services.GetRequiredService<TimeoutDiagnostics>();

        // 启动服务端
        await app.StartAsync();

        return new EmbeddedServer(app, options.Port, processor, diagnostics);
    }

    /// <summary>
    /// 重置服务端统计信息
    /// </summary>
    public void ResetStats()
    {
        _processor.ResetStats();
        _diagnostics.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        // WebApplication.DisposeAsync 会自动处理 DI 容器中的服务
        // 不需要手动 dispose _processor 和 _diagnostics
        await _app.DisposeAsync();
    }
}

/// <summary>
/// 内嵌服务端配置选项
/// </summary>
public class EmbeddedServerOptions
{
    public int Port { get; set; } = 5000;
    public int MinWorkerThreads { get; set; } = 200;
    public int MinIoThreads { get; set; } = 200;
    public int MaxStreamsPerConnection { get; set; } = 500;
    public int MinProcessingTimeUs { get; set; } = 10;
    public int MaxProcessingTimeMs { get; set; } = 50;
}
