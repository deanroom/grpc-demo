using GrpcTimeoutSimulator.Benchmark.Hosting;
using GrpcTimeoutSimulator.Benchmark.Reporting;

namespace GrpcTimeoutSimulator.Benchmark.Benchmarks;

/// <summary>
/// 配置优化探索器
/// 使用启发式搜索找到最佳配置参数
/// </summary>
public class ConfigurationOptimizer
{
    private readonly BenchmarkConfig _config;
    private readonly SteadyStateLoadGenerator _loadGenerator;
    private readonly EmbeddedServer? _server;
    private readonly ConsoleReporter _reporter;

    public ConfigurationOptimizer(
        BenchmarkConfig config,
        SteadyStateLoadGenerator loadGenerator,
        EmbeddedServer? server,
        ConsoleReporter reporter)
    {
        _config = config;
        _loadGenerator = loadGenerator;
        _server = server;
        _reporter = reporter;
    }

    /// <summary>
    /// 执行配置优化探索
    /// </summary>
    public async Task<ConfigOptimizationResult> OptimizeAsync(
        int baselineConcurrency,
        CancellationToken cancellationToken = default)
    {
        var result = new ConfigOptimizationResult();
        var bestMaxConcurrency = baselineConcurrency;
        var bestConfig = CreateCurrentConfig();

        // 配置参数空间
        var channelPoolSizes = new[] { 1, 2, 4, 8 };
        var enableMultipleConnections = new[] { false, true };
        var workerThreadOptions = new[] { 100, 200, 500 };

        // 1. 测试 EnableMultipleHttp2Connections 的影响
        _reporter.PrintInfo("测试 EnableMultipleHttp2Connections 影响...");

        foreach (var enableMultiple in enableMultipleConnections)
        {
            var testConfig = new ClientConfig
            {
                ChannelPoolSize = _config.Client.ChannelPoolSize,
                EnableMultipleHttp2Connections = enableMultiple,
                MaxConnectionsPerServer = _config.Client.MaxConnectionsPerServer
            };

            var testResult = await TestConfigurationAsync(testConfig, baselineConcurrency, cancellationToken);

            result.TestedConfigs.Add(new ConfigTestResult
            {
                ConfigDescription = $"EnableMultipleHttp2Connections={enableMultiple}",
                MaxConcurrency = testResult.maxConcurrency,
                SaturatedThroughput = testResult.throughput,
                ConfigValues = new Dictionary<string, object>
                {
                    ["EnableMultipleHttp2Connections"] = enableMultiple
                }
            });

            if (testResult.maxConcurrency > bestMaxConcurrency)
            {
                bestMaxConcurrency = testResult.maxConcurrency;
                bestConfig = CloneConfig(testConfig);
            }
        }

        // 2. 测试 ChannelPoolSize 的影响
        _reporter.PrintInfo("测试 ChannelPoolSize 影响...");

        foreach (var poolSize in channelPoolSizes)
        {
            var testConfig = new ClientConfig
            {
                ChannelPoolSize = poolSize,
                EnableMultipleHttp2Connections = bestConfig.EnableMultipleHttp2Connections,
                MaxConnectionsPerServer = _config.Client.MaxConnectionsPerServer
            };

            var testResult = await TestConfigurationAsync(testConfig, baselineConcurrency, cancellationToken);

            result.TestedConfigs.Add(new ConfigTestResult
            {
                ConfigDescription = $"ChannelPoolSize={poolSize}",
                MaxConcurrency = testResult.maxConcurrency,
                SaturatedThroughput = testResult.throughput,
                ConfigValues = new Dictionary<string, object>
                {
                    ["ChannelPoolSize"] = poolSize
                }
            });

            if (testResult.maxConcurrency > bestMaxConcurrency)
            {
                bestMaxConcurrency = testResult.maxConcurrency;
                bestConfig = CloneConfig(testConfig);
            }
        }

        // 3. 测试线程池配置的影响（仅内嵌模式）
        if (_server != null)
        {
            _reporter.PrintInfo("测试 ThreadPool 配置影响...");

            foreach (var threads in workerThreadOptions)
            {
                // 设置线程池
                ThreadPool.SetMinThreads(threads, threads);

                var testResult = await TestConfigurationAsync(bestConfig, baselineConcurrency, cancellationToken);

                result.TestedConfigs.Add(new ConfigTestResult
                {
                    ConfigDescription = $"ThreadPool={threads}",
                    MaxConcurrency = testResult.maxConcurrency,
                    SaturatedThroughput = testResult.throughput,
                    ConfigValues = new Dictionary<string, object>
                    {
                        ["MinWorkerThreads"] = threads
                    }
                });

                if (testResult.maxConcurrency > bestMaxConcurrency)
                {
                    bestMaxConcurrency = testResult.maxConcurrency;
                }
            }
        }

        // 恢复原始线程池设置
        ThreadPool.SetMinThreads(_config.Server.MinWorkerThreads, _config.Server.MinIoThreads);

        // 恢复原始客户端配置
        _loadGenerator.ReinitializeChannels(_config.Client);

        // 设置最佳配置
        result.BestConfig = new BestConfig
        {
            EnableMultipleHttp2Connections = bestConfig.EnableMultipleHttp2Connections,
            ChannelPoolSize = bestConfig.ChannelPoolSize,
            MinWorkerThreads = _config.Server.MinWorkerThreads,
            MaxStreamsPerConnection = _config.Server.MaxStreamsPerConnection,
            MaxConcurrency = bestMaxConcurrency,
            SaturatedThroughput = result.TestedConfigs
                .Where(c => c.MaxConcurrency == bestMaxConcurrency)
                .FirstOrDefault()?.SaturatedThroughput ?? 0
        };

        result.ImprovementRatio = baselineConcurrency > 0
            ? (double)(bestMaxConcurrency - baselineConcurrency) / baselineConcurrency
            : 0;

        return result;
    }

    /// <summary>
    /// 测试指定配置的并发能力
    /// </summary>
    private async Task<(int maxConcurrency, double throughput)> TestConfigurationAsync(
        ClientConfig clientConfig,
        int startConcurrency,
        CancellationToken cancellationToken)
    {
        // 使用新配置重新初始化客户端
        _loadGenerator.ReinitializeChannels(clientConfig);
        _server?.ResetStats();

        // 预热
        await _loadGenerator.RunAsync(10, 2, cancellationToken);

        // 快速探测：使用较短的测试时间
        int concurrency = startConcurrency;
        int maxGoodConcurrency = 0;
        double throughput = 0;

        // 先测试起点
        var result = await _loadGenerator.RunAsync(concurrency, 5, cancellationToken);
        var latencyDist = LatencyDistribution.Calculate(result.Latencies);

        if (MeetsSla(result.SuccessRate, latencyDist.P99))
        {
            maxGoodConcurrency = concurrency;
            throughput = result.Throughput;

            // 尝试更高的并发
            concurrency = (int)(concurrency * 1.5);
            result = await _loadGenerator.RunAsync(concurrency, 5, cancellationToken);
            latencyDist = LatencyDistribution.Calculate(result.Latencies);

            while (MeetsSla(result.SuccessRate, latencyDist.P99) && concurrency < _config.Probe.MaxConcurrency)
            {
                maxGoodConcurrency = concurrency;
                throughput = result.Throughput;
                concurrency = (int)(concurrency * 1.3);

                _server?.ResetStats();
                result = await _loadGenerator.RunAsync(concurrency, 5, cancellationToken);
                latencyDist = LatencyDistribution.Calculate(result.Latencies);
            }
        }

        return (maxGoodConcurrency, throughput);
    }

    private bool MeetsSla(double successRate, double p99)
    {
        return successRate >= _config.Sla.SuccessRate && p99 <= _config.Sla.P99ThresholdMs;
    }

    private ClientConfig CreateCurrentConfig()
    {
        return new ClientConfig
        {
            ChannelPoolSize = _config.Client.ChannelPoolSize,
            EnableMultipleHttp2Connections = _config.Client.EnableMultipleHttp2Connections,
            MaxConnectionsPerServer = _config.Client.MaxConnectionsPerServer
        };
    }

    private static ClientConfig CloneConfig(ClientConfig config)
    {
        return new ClientConfig
        {
            ChannelPoolSize = config.ChannelPoolSize,
            EnableMultipleHttp2Connections = config.EnableMultipleHttp2Connections,
            MaxConnectionsPerServer = config.MaxConnectionsPerServer
        };
    }
}
