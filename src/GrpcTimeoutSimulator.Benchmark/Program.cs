using System.CommandLine;
using GrpcTimeoutSimulator.Benchmark.Benchmarks;

// 创建根命令
var rootCommand = new RootCommand("gRPC 并发能力探测与性能评估工具");

// 定义选项
var modeOption = new Option<string>(
    name: "--mode",
    getDefaultValue: () => "auto",
    description: "测试模式：auto（自动探测）或 manual（手动测试）");

var concurrencyOption = new Option<string>(
    name: "--concurrency",
    getDefaultValue: () => "50,100,200,500",
    description: "手动模式下的并发级别列表（逗号分隔）");

var externalServerOption = new Option<string?>(
    name: "--external-server",
    description: "外部服务端地址（默认使用内嵌服务端）");

var successRateOption = new Option<double>(
    name: "--success-rate",
    getDefaultValue: () => 0.999,
    description: "SLA 成功率阈值（默认 0.999 即 99.9%）");

var p99ThresholdOption = new Option<int>(
    name: "--p99-threshold",
    getDefaultValue: () => 500,
    description: "SLA P99 延迟阈值（毫秒，默认 500）");

var warmupDurationOption = new Option<int>(
    name: "--warmup-duration",
    getDefaultValue: () => 5,
    description: "预热时长（秒）");

var testDurationOption = new Option<int>(
    name: "--test-duration",
    getDefaultValue: () => 10,
    description: "每个并发级别测试时长（秒）");

var stabilityDurationOption = new Option<int>(
    name: "--stability-duration",
    getDefaultValue: () => 30,
    description: "稳定性验证时长（秒）");

var portOption = new Option<int>(
    name: "--port",
    getDefaultValue: () => 5000,
    description: "内嵌服务端端口");

var channelPoolSizeOption = new Option<int>(
    name: "--channel-pool-size",
    getDefaultValue: () => 4,
    description: "gRPC Channel 池大小");

var requestTimeoutOption = new Option<int>(
    name: "--request-timeout",
    getDefaultValue: () => 5000,
    description: "请求超时时间（毫秒）");

// 添加选项到命令
rootCommand.AddOption(modeOption);
rootCommand.AddOption(concurrencyOption);
rootCommand.AddOption(externalServerOption);
rootCommand.AddOption(successRateOption);
rootCommand.AddOption(p99ThresholdOption);
rootCommand.AddOption(warmupDurationOption);
rootCommand.AddOption(testDurationOption);
rootCommand.AddOption(stabilityDurationOption);
rootCommand.AddOption(portOption);
rootCommand.AddOption(channelPoolSizeOption);
rootCommand.AddOption(requestTimeoutOption);

// 设置处理器
rootCommand.SetHandler(async (context) =>
{
    var mode = context.ParseResult.GetValueForOption(modeOption)!;
    var concurrency = context.ParseResult.GetValueForOption(concurrencyOption)!;
    var externalServer = context.ParseResult.GetValueForOption(externalServerOption);
    var successRate = context.ParseResult.GetValueForOption(successRateOption);
    var p99Threshold = context.ParseResult.GetValueForOption(p99ThresholdOption);
    var warmupDuration = context.ParseResult.GetValueForOption(warmupDurationOption);
    var testDuration = context.ParseResult.GetValueForOption(testDurationOption);
    var stabilityDuration = context.ParseResult.GetValueForOption(stabilityDurationOption);
    var port = context.ParseResult.GetValueForOption(portOption);
    var channelPoolSize = context.ParseResult.GetValueForOption(channelPoolSizeOption);
    var requestTimeout = context.ParseResult.GetValueForOption(requestTimeoutOption);

    // 解析并发级别
    var concurrencyLevels = concurrency
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.Parse(s.Trim()))
        .ToArray();

    // 创建配置
    var config = new BenchmarkConfig
    {
        Mode = mode.ToLower() == "manual" ? BenchmarkMode.Manual : BenchmarkMode.Auto,
        ManualConcurrencyLevels = concurrencyLevels,
        ExternalServerAddress = externalServer,
        Sla = new SlaConfig
        {
            SuccessRate = successRate,
            P99ThresholdMs = p99Threshold
        },
        Probe = new ProbeConfig
        {
            WarmupDurationSec = warmupDuration,
            TestDurationSec = testDuration,
            StabilityDurationSec = stabilityDuration,
            RequestTimeoutMs = requestTimeout
        },
        Client = new ClientConfig
        {
            ChannelPoolSize = channelPoolSize,
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 100
        },
        Server = new ServerConfig
        {
            Port = port,
            MinWorkerThreads = 200,
            MinIoThreads = 200,
            MaxStreamsPerConnection = 500
        }
    };

    // 设置取消处理
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n正在停止测试...");
        cts.Cancel();
    };

    // 运行基准测试
    var runner = new BenchmarkRunner(config);
    await runner.RunAsync(cts.Token);
});

// 执行命令
return await rootCommand.InvokeAsync(args);
