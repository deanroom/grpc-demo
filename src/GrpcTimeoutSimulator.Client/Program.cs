using System.CommandLine;
using GrpcTimeoutSimulator.Client.Diagnostics;
using GrpcTimeoutSimulator.Client.LoadGenerators;

// 优化线程池（高并发场景必需）
// 避免线程池饥饿导致的请求排队
ThreadPool.SetMinThreads(workerThreads: 500, completionPortThreads: 500);

// 定义命令行参数
var burstSizeOption = new Option<int>(
    name: "--burst-size",
    description: "每次突发的请求数",
    getDefaultValue: () => 100);

var burstCountOption = new Option<int>(
    name: "--burst-count",
    description: "突发次数",
    getDefaultValue: () => 10);

var burstIntervalOption = new Option<int>(
    name: "--burst-interval",
    description: "突发内请求间隔（ms）",
    getDefaultValue: () => 1);

var burstGapOption = new Option<int>(
    name: "--burst-gap",
    description: "突发之间的间隔（ms）",
    getDefaultValue: () => 500);

var deadlineOption = new Option<int>(
    name: "--deadline",
    description: "超时时间（ms）",
    getDefaultValue: () => 3000);

var serverOption = new Option<string>(
    name: "--server",
    description: "服务端地址",
    getDefaultValue: () => "http://localhost:5000");

var asyncOption = new Option<bool>(
    name: "--async",
    description: "使用异步调用模式",
    getDefaultValue: () => false);

var rootCommand = new RootCommand("gRPC 超时仿真客户端");
rootCommand.AddOption(burstSizeOption);
rootCommand.AddOption(burstCountOption);
rootCommand.AddOption(burstIntervalOption);
rootCommand.AddOption(burstGapOption);
rootCommand.AddOption(deadlineOption);
rootCommand.AddOption(serverOption);
rootCommand.AddOption(asyncOption);

rootCommand.SetHandler(async (int burstSize, int burstCount, int burstInterval, int burstGap, int deadline, string server, bool useAsync) =>
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════════════════╗");
    Console.WriteLine("║    gRPC 超时仿真客户端                    ║");
    Console.WriteLine("╚══════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();

    var config = new LoadConfig
    {
        BurstSize = burstSize,
        BurstCount = burstCount,
        BurstIntervalMs = burstInterval,
        BurstGapMs = burstGap,
        DeadlineMs = deadline,
        ServerAddress = server,
        UseSyncCalls = !useAsync
    };

    var diagnostics = new ClientDiagnostics();
    var generator = new BurstLoadGenerator(config, diagnostics);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n正在停止测试...");
        cts.Cancel();
    };

    try
    {
        await generator.RunAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("测试已取消");
    }
    finally
    {
        diagnostics.PrintFinalReport();
        generator.Dispose();
    }

}, burstSizeOption, burstCountOption, burstIntervalOption, burstGapOption, deadlineOption, serverOption, asyncOption);

return await rootCommand.InvokeAsync(args);
