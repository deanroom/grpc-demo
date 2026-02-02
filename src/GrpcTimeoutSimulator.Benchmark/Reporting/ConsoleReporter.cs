using GrpcTimeoutSimulator.Benchmark.Benchmarks;

namespace GrpcTimeoutSimulator.Benchmark.Reporting;

/// <summary>
/// 控制台报告生成器
/// </summary>
public class ConsoleReporter
{
    private readonly SlaConfig _sla;

    public ConsoleReporter(SlaConfig sla)
    {
        _sla = sla;
    }

    /// <summary>
    /// 打印标题
    /// </summary>
    public void PrintHeader()
    {
        Console.WriteLine();
        PrintBoxTop();
        PrintBoxLine("gRPC 并发能力探测与性能评估", true);
        PrintBoxBottom();
        Console.WriteLine();
    }

    /// <summary>
    /// 打印配置信息
    /// </summary>
    public void PrintConfig(string serverAddress, bool isEmbedded)
    {
        Console.WriteLine($"  模式: {(isEmbedded ? "内嵌服务端" : "外部服务端")} ({serverAddress})");
        Console.WriteLine($"  SLA: 成功率 >= {_sla.SuccessRate:P1}, P99 <= {_sla.P99ThresholdMs}ms");
        Console.WriteLine();
    }

    /// <summary>
    /// 打印阶段标题
    /// </summary>
    public void PrintPhaseHeader(string phaseName, string details)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($">>> {phaseName}");
        if (!string.IsNullOrEmpty(details))
        {
            Console.Write($" ({details})");
        }
        Console.WriteLine();
        Console.ResetColor();
    }

    /// <summary>
    /// 打印服务端启动信息
    /// </summary>
    public void PrintServerStarted(string address)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    服务端已启动: {address}");
        Console.ResetColor();
    }

    /// <summary>
    /// 打印测试结果
    /// </summary>
    public void PrintTestResult(ConcurrencyTestResult result)
    {
        var statusSymbol = result.MeetsSla ? "✓" : "✗";
        var statusColor = result.MeetsSla ? ConsoleColor.Green : ConsoleColor.Red;

        Console.Write($"    [{result.Concurrency,4} 并发] ");
        Console.Write($"成功率: {result.SuccessRate,6:P1} | ");
        Console.Write($"P99: {result.Latency.P99,5:F0}ms | ");
        Console.Write($"吞吐: {result.Throughput,5:F0} req/s  ");

        Console.ForegroundColor = statusColor;
        Console.Write(statusSymbol);
        Console.ResetColor();

        Console.WriteLine();
    }

    /// <summary>
    /// 打印信息
    /// </summary>
    public void PrintInfo(string message)
    {
        Console.WriteLine($"    {message}");
    }

    /// <summary>
    /// 打印警告
    /// </summary>
    public void PrintWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"    ⚠ {message}");
        Console.ResetColor();
    }

    /// <summary>
    /// 打印错误
    /// </summary>
    public void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    ✗ {message}");
        Console.ResetColor();
    }

    /// <summary>
    /// 打印探测结果摘要
    /// </summary>
    public void PrintProbeSummary(ProbeResult result)
    {
        Console.WriteLine();
        PrintSeparator();
        PrintCenterLine("环境并发能力评估");
        PrintSeparator();
        Console.WriteLine();

        PrintResultBox(result);
        Console.WriteLine();
    }

    /// <summary>
    /// 打印手动测试结果
    /// </summary>
    public void PrintManualResults(List<ConcurrencyTestResult> results)
    {
        Console.WriteLine();
        PrintSeparator();
        PrintCenterLine("手动测试结果");
        PrintSeparator();
        Console.WriteLine();

        Console.WriteLine("  ┌─────────────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │ 并发数 │ 成功率   │ P50     │ P99     │ 吞吐量     │ 状态    │");
        Console.WriteLine("  ├─────────────────────────────────────────────────────────────────────────┤");

        foreach (var result in results)
        {
            var status = result.MeetsSla ? "✓ 满足" : "✗ 不满足";
            var statusColor = result.MeetsSla ? ConsoleColor.Green : ConsoleColor.Red;

            Console.Write($"  │ {result.Concurrency,6} │ {result.SuccessRate,7:P1} │ {result.Latency.P50,6:F0}ms │ {result.Latency.P99,6:F0}ms │ {result.Throughput,8:F0} r/s │ ");
            Console.ForegroundColor = statusColor;
            Console.Write($"{status,-7}");
            Console.ResetColor();
            Console.WriteLine(" │");
        }

        Console.WriteLine("  └─────────────────────────────────────────────────────────────────────────┘");
    }

    private void PrintResultBox(ProbeResult result)
    {
        Console.WriteLine("  ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine($"  │ 最大并发能力:      {result.MaxConcurrency,-4} (成功率 {_sla.SuccessRate:P1}+)              │");
        Console.WriteLine($"  │ 有效并发能力:      {result.EffectiveConcurrency,-4} (满足 P99 < {_sla.P99ThresholdMs}ms)           │");
        Console.WriteLine($"  │ 饱和吞吐量:        {result.SaturatedThroughput,-5:F0} req/s                          │");
        Console.WriteLine("  │                                                         │");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  │ ★ 建议生产环境并发上限: {result.RecommendedConcurrencyLimit,-4} (80% 水位)             │");
        Console.ResetColor();
        Console.WriteLine("  └─────────────────────────────────────────────────────────┘");
    }

    private void PrintBoxTop()
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    }

    private void PrintBoxBottom()
    {
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    }

    private void PrintBoxLine(string text, bool center = false)
    {
        const int width = 56;
        if (center)
        {
            var padding = (width - text.Length) / 2;
            var paddedText = text.PadLeft(padding + text.Length).PadRight(width);
            Console.WriteLine($"║{paddedText}║");
        }
        else
        {
            Console.WriteLine($"║ {text.PadRight(width - 1)}║");
        }
    }

    private void PrintSeparator()
    {
        Console.WriteLine("══════════════════════════════════════════════════════════════");
    }

    private void PrintCenterLine(string text)
    {
        const int width = 60;
        var padding = (width - text.Length) / 2;
        Console.WriteLine(text.PadLeft(padding + text.Length).PadRight(width));
    }
}
