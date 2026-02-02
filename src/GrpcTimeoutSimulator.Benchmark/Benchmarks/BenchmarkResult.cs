namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

/// <summary>
/// 单次并发测试结果
/// </summary>
public class ConcurrencyTestResult
{
    /// <summary>
    /// 并发级别
    /// </summary>
    public int Concurrency { get; set; }

    /// <summary>
    /// 测试时长（秒）
    /// </summary>
    public double DurationSec { get; set; }

    /// <summary>
    /// 总请求数
    /// </summary>
    public int TotalRequests { get; set; }

    /// <summary>
    /// 成功请求数
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// 超时请求数
    /// </summary>
    public int TimeoutCount { get; set; }

    /// <summary>
    /// 错误请求数
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// 成功率
    /// </summary>
    public double SuccessRate => TotalRequests > 0 ? (double)SuccessCount / TotalRequests : 0;

    /// <summary>
    /// 吞吐量（请求/秒）
    /// </summary>
    public double Throughput => DurationSec > 0 ? SuccessCount / DurationSec : 0;

    /// <summary>
    /// 延迟分布
    /// </summary>
    public LatencyDistribution Latency { get; set; } = new();

    /// <summary>
    /// 资源快照
    /// </summary>
    public ResourceSnapshot Resource { get; set; } = new();

    /// <summary>
    /// 是否满足 SLA
    /// </summary>
    public bool MeetsSla { get; set; }

    /// <summary>
    /// SLA 不满足的原因
    /// </summary>
    public string? SlaViolationReason { get; set; }

    /// <summary>
    /// 超时原因分析
    /// </summary>
    public TimeoutAnalysis Timeout { get; set; } = new();
}

/// <summary>
/// 超时原因分析
/// </summary>
public class TimeoutAnalysis
{
    /// <summary>
    /// HTTP/2 连接层超时数量（请求未到达服务端）
    /// </summary>
    public int Http2LayerTimeoutCount { get; set; }

    /// <summary>
    /// 服务端应用层超时数量
    /// </summary>
    public int ServerLayerTimeoutCount { get; set; }

    /// <summary>
    /// 平均队列等待时间（成功请求）
    /// </summary>
    public double AvgQueueWaitMs { get; set; }

    /// <summary>
    /// P99 队列等待时间（成功请求）
    /// </summary>
    public double P99QueueWaitMs { get; set; }

    /// <summary>
    /// 是否主要是客户端瓶颈
    /// </summary>
    public bool IsClientBottleneck => Http2LayerTimeoutCount > ServerLayerTimeoutCount;

    /// <summary>
    /// 瓶颈描述
    /// </summary>
    public string BottleneckDescription
    {
        get
        {
            if (Http2LayerTimeoutCount == 0 && ServerLayerTimeoutCount == 0)
                return "无超时";

            if (IsClientBottleneck)
                return $"客户端 HTTP/2 连接层瓶颈（{Http2LayerTimeoutCount} 次超时）";

            return $"服务端应用层瓶颈（{ServerLayerTimeoutCount} 次超时）";
        }
    }
}

/// <summary>
/// 延迟分布
/// </summary>
public class LatencyDistribution
{
    /// <summary>
    /// 最小延迟（毫秒）
    /// </summary>
    public double Min { get; set; }

    /// <summary>
    /// 最大延迟（毫秒）
    /// </summary>
    public double Max { get; set; }

    /// <summary>
    /// 平均延迟（毫秒）
    /// </summary>
    public double Mean { get; set; }

    /// <summary>
    /// P50 延迟（毫秒）
    /// </summary>
    public double P50 { get; set; }

    /// <summary>
    /// P90 延迟（毫秒）
    /// </summary>
    public double P90 { get; set; }

    /// <summary>
    /// P95 延迟（毫秒）
    /// </summary>
    public double P95 { get; set; }

    /// <summary>
    /// P99 延迟（毫秒）
    /// </summary>
    public double P99 { get; set; }

    /// <summary>
    /// 标准差（毫秒）
    /// </summary>
    public double StdDev { get; set; }

    /// <summary>
    /// 所有延迟值（用于计算）
    /// </summary>
    internal List<double> AllLatencies { get; set; } = [];

    /// <summary>
    /// 从延迟列表计算分布
    /// </summary>
    public static LatencyDistribution Calculate(List<double> latencies)
    {
        if (latencies.Count == 0)
        {
            return new LatencyDistribution();
        }

        var sorted = latencies.OrderBy(x => x).ToList();
        var mean = sorted.Average();
        var variance = sorted.Average(x => Math.Pow(x - mean, 2));

        return new LatencyDistribution
        {
            Min = sorted[0],
            Max = sorted[^1],
            Mean = mean,
            P50 = GetPercentile(sorted, 50),
            P90 = GetPercentile(sorted, 90),
            P95 = GetPercentile(sorted, 95),
            P99 = GetPercentile(sorted, 99),
            StdDev = Math.Sqrt(variance),
            AllLatencies = latencies
        };
    }

    private static double GetPercentile(List<double> sorted, int percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
}

/// <summary>
/// 资源快照
/// </summary>
public class ResourceSnapshot
{
    /// <summary>
    /// 峰值队列深度
    /// </summary>
    public int PeakQueueDepth { get; set; }

    /// <summary>
    /// 最大队列等待时间（毫秒）
    /// </summary>
    public double MaxQueueWaitTimeMs { get; set; }

    /// <summary>
    /// GC Gen0 次数
    /// </summary>
    public int GcGen0Count { get; set; }

    /// <summary>
    /// GC Gen1 次数
    /// </summary>
    public int GcGen1Count { get; set; }

    /// <summary>
    /// GC Gen2 次数
    /// </summary>
    public int GcGen2Count { get; set; }

    /// <summary>
    /// 最小可用工作线程
    /// </summary>
    public int MinAvailableWorkerThreads { get; set; }

    /// <summary>
    /// 最小可用 IO 线程
    /// </summary>
    public int MinAvailableIoThreads { get; set; }
}

/// <summary>
/// 并发探测结果
/// </summary>
public class ProbeResult
{
    /// <summary>
    /// 最大并发数（满足成功率要求）
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// 有效并发数（同时满足成功率和 P99 延迟要求）
    /// </summary>
    public int EffectiveConcurrency { get; set; }

    /// <summary>
    /// 饱和吞吐量（有效并发时的请求/秒）
    /// </summary>
    public double SaturatedThroughput { get; set; }

    /// <summary>
    /// 建议生产环境并发上限（有效并发 × 0.8）
    /// </summary>
    public int RecommendedConcurrencyLimit => (int)(EffectiveConcurrency * 0.8);

    /// <summary>
    /// 所有测试结果
    /// </summary>
    public List<ConcurrencyTestResult> AllResults { get; set; } = [];

    /// <summary>
    /// 稳定性验证结果
    /// </summary>
    public ConcurrencyTestResult? StabilityResult { get; set; }

    /// <summary>
    /// 探测耗时（秒）
    /// </summary>
    public double TotalDurationSec { get; set; }
}

/// <summary>
/// 配置优化结果
/// </summary>
public class ConfigOptimizationResult
{
    /// <summary>
    /// 测试的配置组合
    /// </summary>
    public List<ConfigTestResult> TestedConfigs { get; set; } = [];

    /// <summary>
    /// 最佳配置
    /// </summary>
    public BestConfig? BestConfig { get; set; }

    /// <summary>
    /// 优化后的提升比例
    /// </summary>
    public double ImprovementRatio { get; set; }
}

/// <summary>
/// 配置测试结果
/// </summary>
public class ConfigTestResult
{
    /// <summary>
    /// 配置描述
    /// </summary>
    public string ConfigDescription { get; set; } = "";

    /// <summary>
    /// 最大并发数
    /// </summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// 饱和吞吐量
    /// </summary>
    public double SaturatedThroughput { get; set; }

    /// <summary>
    /// 配置值
    /// </summary>
    public Dictionary<string, object> ConfigValues { get; set; } = [];
}

/// <summary>
/// 最佳配置
/// </summary>
public class BestConfig
{
    public bool EnableMultipleHttp2Connections { get; set; }
    public int ChannelPoolSize { get; set; }
    public int MinWorkerThreads { get; set; }
    public int MaxStreamsPerConnection { get; set; }
    public int MaxConcurrency { get; set; }
    public double SaturatedThroughput { get; set; }
}

/// <summary>
/// 完整基准测试报告
/// </summary>
public class BenchmarkReport
{
    /// <summary>
    /// 测试开始时间
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 测试结束时间
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// 服务端地址
    /// </summary>
    public string ServerAddress { get; set; } = "";

    /// <summary>
    /// 是否使用内嵌服务端
    /// </summary>
    public bool IsEmbeddedServer { get; set; }

    /// <summary>
    /// SLA 配置
    /// </summary>
    public SlaConfig Sla { get; set; } = new();

    /// <summary>
    /// 探测结果
    /// </summary>
    public ProbeResult? ProbeResult { get; set; }

    /// <summary>
    /// 手动测试结果
    /// </summary>
    public List<ConcurrencyTestResult>? ManualResults { get; set; }

    /// <summary>
    /// 配置优化结果
    /// </summary>
    public ConfigOptimizationResult? OptimizationResult { get; set; }
}
