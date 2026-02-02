namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

/// <summary>
/// 基准测试配置
/// </summary>
public class BenchmarkConfig
{
    /// <summary>
    /// 测试模式
    /// </summary>
    public BenchmarkMode Mode { get; set; } = BenchmarkMode.Auto;

    /// <summary>
    /// 手动模式下的并发级别列表
    /// </summary>
    public int[] ManualConcurrencyLevels { get; set; } = [50, 100, 200, 500];

    /// <summary>
    /// 外部服务端地址（为 null 时使用内嵌服务端）
    /// </summary>
    public string? ExternalServerAddress { get; set; }

    /// <summary>
    /// 是否启用配置优化探索
    /// </summary>
    public bool OptimizeConfig { get; set; }

    /// <summary>
    /// SLA 配置
    /// </summary>
    public SlaConfig Sla { get; set; } = new();

    /// <summary>
    /// 探测配置
    /// </summary>
    public ProbeConfig Probe { get; set; } = new();

    /// <summary>
    /// 客户端配置
    /// </summary>
    public ClientConfig Client { get; set; } = new();

    /// <summary>
    /// 服务端配置（仅内嵌模式有效）
    /// </summary>
    public ServerConfig Server { get; set; } = new();
}

/// <summary>
/// 测试模式
/// </summary>
public enum BenchmarkMode
{
    /// <summary>
    /// 自动探测模式：使用二分搜索找到最大并发
    /// </summary>
    Auto,

    /// <summary>
    /// 手动模式：测试指定的并发级别
    /// </summary>
    Manual
}

/// <summary>
/// SLA 配置
/// </summary>
public class SlaConfig
{
    /// <summary>
    /// 成功率阈值（默认 99.9%）
    /// </summary>
    public double SuccessRate { get; set; } = 0.999;

    /// <summary>
    /// P99 延迟阈值（毫秒，默认 200ms）
    /// </summary>
    public int P99ThresholdMs { get; set; } = 200;
}

/// <summary>
/// 探测配置
/// </summary>
public class ProbeConfig
{
    /// <summary>
    /// 预热时长（秒）
    /// </summary>
    public int WarmupDurationSec { get; set; } = 5;

    /// <summary>
    /// 预热并发数
    /// </summary>
    public int WarmupConcurrency { get; set; } = 10;

    /// <summary>
    /// 每个并发级别测试时长（秒）
    /// </summary>
    public int TestDurationSec { get; set; } = 10;

    /// <summary>
    /// 稳定性验证时长（秒）
    /// </summary>
    public int StabilityDurationSec { get; set; } = 30;

    /// <summary>
    /// 初始并发数（指数增长起点）
    /// </summary>
    public int InitialConcurrency { get; set; } = 20;

    /// <summary>
    /// 最大探测并发数
    /// </summary>
    public int MaxConcurrency { get; set; } = 5000;

    /// <summary>
    /// 请求超时时间（毫秒）
    /// </summary>
    public int RequestTimeoutMs { get; set; } = 5000;
}

/// <summary>
/// 客户端配置
/// </summary>
public class ClientConfig
{
    /// <summary>
    /// Channel 池大小
    /// </summary>
    public int ChannelPoolSize { get; set; } = 4;

    /// <summary>
    /// 启用多 HTTP/2 连接
    /// </summary>
    public bool EnableMultipleHttp2Connections { get; set; } = true;

    /// <summary>
    /// 每服务器最大连接数
    /// </summary>
    public int MaxConnectionsPerServer { get; set; } = 100;
}

/// <summary>
/// 服务端配置（内嵌模式）
/// </summary>
public class ServerConfig
{
    /// <summary>
    /// 服务端口
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// 最小工作线程数
    /// </summary>
    public int MinWorkerThreads { get; set; } = 200;

    /// <summary>
    /// 最小 IO 线程数
    /// </summary>
    public int MinIoThreads { get; set; } = 200;

    /// <summary>
    /// 每连接最大流数
    /// </summary>
    public int MaxStreamsPerConnection { get; set; } = 500;

    /// <summary>
    /// 最小处理时间（微秒）
    /// </summary>
    public int MinProcessingTimeUs { get; set; } = 10;

    /// <summary>
    /// 最大处理时间（毫秒）
    /// </summary>
    public int MaxProcessingTimeMs { get; set; } = 50;
}
