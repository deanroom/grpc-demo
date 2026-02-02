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

        // 如果有超时，显示超时原因
        if (result.TimeoutCount > 0 && !result.MeetsSla)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            if (result.Timeout.Http2LayerTimeoutCount > 0)
            {
                Console.Write($" [HTTP/2层:{result.Timeout.Http2LayerTimeoutCount}]");
            }
            if (result.Timeout.ServerLayerTimeoutCount > 0)
            {
                Console.Write($" [服务端:{result.Timeout.ServerLayerTimeoutCount}]");
            }
            Console.ResetColor();
        }

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
    /// 打印配置优化结果
    /// </summary>
    public void PrintOptimizationResult(ConfigOptimizationResult? result, int baselineConcurrency)
    {
        if (result == null) return;

        Console.WriteLine();
        PrintPhaseHeader("配置优化探索", "--optimize-config");

        foreach (var config in result.TestedConfigs)
        {
            var improvement = baselineConcurrency > 0
                ? $" ({(config.MaxConcurrency > baselineConcurrency ? "+" : "")}{(double)(config.MaxConcurrency - baselineConcurrency) / baselineConcurrency:P0})"
                : "";

            Console.WriteLine($"    测试配置: {config.ConfigDescription} → 最大并发 {config.MaxConcurrency}{improvement}");
        }

        if (result.BestConfig != null)
        {
            Console.WriteLine();
            PrintOptimalConfigBox(result.BestConfig, baselineConcurrency);
        }
    }

    /// <summary>
    /// 打印优化建议
    /// </summary>
    public void PrintOptimizationSuggestions(ProbeResult result)
    {
        Console.WriteLine();
        Console.WriteLine("  优化建议:");

        // 分析失败的测试结果，找出超时原因
        var failedResults = result.AllResults.Where(r => !r.MeetsSla && r.TimeoutCount > 0).ToList();

        // 统计超时原因
        int totalHttp2Timeouts = failedResults.Sum(r => r.Timeout.Http2LayerTimeoutCount);
        int totalServerTimeouts = failedResults.Sum(r => r.Timeout.ServerLayerTimeoutCount);

        // 分析最高成功并发的结果
        var bestResult = result.AllResults
            .Where(r => r.MeetsSla)
            .OrderByDescending(r => r.Concurrency)
            .FirstOrDefault();

        if (totalHttp2Timeouts > totalServerTimeouts && totalHttp2Timeouts > 0)
        {
            // 主要是客户端 HTTP/2 层瓶颈
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("    ★ 主要瓶颈: 客户端 HTTP/2 连接层");
            Console.ResetColor();
            Console.WriteLine($"      - HTTP/2 层超时: {totalHttp2Timeouts} 次");
            Console.WriteLine($"      - 服务端层超时: {totalServerTimeouts} 次");
            Console.WriteLine();
            Console.WriteLine("    建议优化:");
            Console.WriteLine("      1. 确保 EnableMultipleHttp2Connections = true (关键!)");
            Console.WriteLine("      2. 增加 Channel 池大小 (ChannelPoolSize)");
            Console.WriteLine("      3. 增加 MaxConnectionsPerServer");
            Console.WriteLine("      4. 预热线程池: ThreadPool.SetMinThreads(500, 500)");
        }
        else if (bestResult != null)
        {
            // 分析服务端瓶颈
            var queueWaitRatio = bestResult.Latency.P99 > 0
                ? bestResult.Resource.MaxQueueWaitTimeMs / bestResult.Latency.P99
                : 0;

            if (queueWaitRatio > 0.5)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("    ★ 主要瓶颈: 服务端队列等待");
                Console.ResetColor();
                Console.WriteLine($"      - 队列等待时间占比: {queueWaitRatio:P0}");
                Console.WriteLine($"      - 峰值队列深度: {bestResult.Resource.PeakQueueDepth}");
                Console.WriteLine();
                Console.WriteLine("    建议优化:");
                Console.WriteLine("      1. 增加处理线程数或使用多队列分片");
                Console.WriteLine("      2. 优化处理逻辑，减少单请求处理时间");
            }
            else
            {
                Console.WriteLine("    当前性能已较优化，可考虑:");
                Console.WriteLine("      1. 增加 Channel 池大小以支持更高并发");
                Console.WriteLine("      2. 确保 EnableMultipleHttp2Connections 已启用");
            }
        }

        // 显示关键配置提示
        Console.WriteLine();
        Console.WriteLine("  关键配置检查:");
        Console.WriteLine("    客户端: EnableMultipleHttp2Connections = true");
        Console.WriteLine("    客户端: ChannelPoolSize >= 4");
        Console.WriteLine("    服务端: MaxStreamsPerConnection >= 500");
        Console.WriteLine("    双端:   ThreadPool.SetMinThreads(200, 200)");
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

    private void PrintOptimalConfigBox(BestConfig config, int baselineConcurrency)
    {
        var improvement = baselineConcurrency > 0
            ? (double)(config.MaxConcurrency - baselineConcurrency) / baselineConcurrency
            : 0;

        Console.WriteLine("  ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │                   最佳配置推荐                          │");
        Console.WriteLine("  ├─────────────────────────────────────────────────────────┤");
        Console.WriteLine($"  │ EnableMultipleHttp2Connections: {config.EnableMultipleHttp2Connections,-20} │");
        Console.WriteLine($"  │ ChannelPoolSize: {config.ChannelPoolSize,-35} │");
        Console.WriteLine($"  │ ThreadPool.SetMinThreads: {config.MinWorkerThreads,-26} │");
        Console.WriteLine("  │                                                         │");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  │ 优化后最大并发: {config.MaxConcurrency,-4} (提升 {improvement:P0})                    │");
        Console.WriteLine($"  │ 优化后饱和吞吐: {config.SaturatedThroughput,-5:F0} req/s                          │");
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
