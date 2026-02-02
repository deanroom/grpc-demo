# gRPC 并发能力探测与性能评估设计文档

## 概述

本文档描述 gRPC 并发能力探测与性能评估工具的设计，该工具可以在运行时自动检测当前环境的并发能力和延迟指标，提供明确的性能评估结果，并支持自动探索最佳配置参数。

## 架构设计

### 进程内集成模式

将 gRPC 服务端和客户端集成到**同一进程**中，简化部署和测试：

```
┌─────────────────────────────────────────────────────────────┐
│                    GrpcBenchmark 进程                        │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────────┐      gRPC/HTTP2      ┌──────────────┐ │
│  │   Benchmark     │  ◄──────────────────► │   Kestrel    │ │
│  │   Client        │   localhost:5000      │   Server     │ │
│  │                 │                       │              │ │
│  │  - 负载生成器    │                       │  - 队列处理  │ │
│  │  - 并发探测器    │                       │  - 诊断系统  │ │
│  │  - 配置优化器    │                       │              │ │
│  └─────────────────┘                       └──────────────┘ │
│                                                             │
│  ┌─────────────────────────────────────────────────────────┐│
│  │                    报告生成器                            ││
│  └─────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────┘
```

**优势：**
- 单命令启动，无需分别启动服务端和客户端
- 消除网络变量干扰，专注测试应用层性能
- 便于 CI/CD 集成

## 核心指标定义

| 指标 | 定义 | 意义 |
|------|------|------|
| **最大并发数** | 保持成功率 ≥ 99.9% 时的最大并发请求数 | 系统极限 |
| **有效并发数** | 同时满足 P99 延迟 < 200ms 的最大并发数 | **实际可用并发** |
| **饱和吞吐量** | 有效并发时的请求/秒 | 容量规划依据 |
| **建议并发上限** | 有效并发数 × 0.8 | 生产环境安全水位 |

**默认 SLA（可通过命令行参数配置）：**
- 成功率 ≥ 99.9%（`--success-rate`）
- P99 延迟 ≤ 200ms（`--p99-threshold`）

## 项目结构

```
src/GrpcTimeoutSimulator.Benchmark/
├── Program.cs                        # 入口：启动服务端 + 运行测试
├── Hosting/
│   └── EmbeddedServer.cs             # 内嵌服务端启动器
├── Benchmarks/
│   ├── BenchmarkConfig.cs            # 配置模型（含 SLA 参数）
│   ├── BenchmarkResult.cs            # 结果模型
│   ├── BenchmarkRunner.cs            # 基准测试运行器
│   ├── ConcurrencyProber.cs          # 并发探测器（核心算法）
│   ├── SteadyStateLoadGenerator.cs   # 稳态负载生成器
│   └── ConfigurationOptimizer.cs     # 配置优化探索器
└── Reporting/
    └── ConsoleReporter.cs            # 控制台报告
```

## 并发探测算法

采用**自适应二分搜索**：

```
Phase 1: 预热 (10 并发 × 5秒)
    ↓
Phase 2: 指数增长 (20 → 40 → 80 → ... 直到失败)
    ↓
Phase 3: 二分搜索 (在 [lastGood, firstBad] 精确定位)
    ↓
Phase 4: 稳定性验证 (边界点 × 30秒)
    ↓
Phase 5: 生成报告
```

### SLA 判定条件

默认值（可通过命令行参数配置）：
- 成功率 ≥ 99.9%（`--success-rate 0.999`）
- P99 延迟 ≤ 200ms（`--p99-threshold 200`）

### 二分搜索精度

当 `high - low <= 10` 时停止搜索，避免过度精确测试带来的时间成本。

## 稳态负载生成器

与 BurstLoadGenerator 不同，SteadyStateLoadGenerator 特点：

1. **并发控制**：使用 `SemaphoreSlim` 严格控制并发数
2. **持续负载**：在测试时间内持续发送请求
3. **非突发模式**：更准确反映稳态性能

```csharp
// 核心逻辑
while (stopwatch.Elapsed < testDuration)
{
    await semaphore.WaitAsync();
    _ = Task.Run(async () => {
        try { await SendRequestAsync(); }
        finally { semaphore.Release(); }
    });
}
```

## 配置优化探索

### 参数空间

```
配置参数空间：
├── 线程池大小: [100, 200, 500]
├── Channel 池大小: [1, 2, 4, 8]
├── EnableMultipleHttp2Connections: [true, false]
└── MaxStreamsPerConnection: [100, 200, 500]
```

### 探索策略

启发式搜索（非穷举）：
1. 先测试关键配置 (EnableMultipleHttp2Connections)
2. 再优化 Channel 池大小
3. 最后微调线程池参数

每个配置使用较短的测试时间（5秒），快速评估影响。

## 命令行使用

```bash
# 自动探测模式（默认）
dotnet run --project src/GrpcTimeoutSimulator.Benchmark

# 自定义 SLA 阈值
dotnet run --project src/GrpcTimeoutSimulator.Benchmark -- \
    --p99-threshold 100 \
    --success-rate 0.999

# 手动模式（测试固定并发级别）
dotnet run --project src/GrpcTimeoutSimulator.Benchmark -- \
    --mode manual \
    --concurrency 50,100,200,500

# 启用配置优化探索
dotnet run --project src/GrpcTimeoutSimulator.Benchmark -- \
    --optimize-config

# 连接外部服务端
dotnet run --project src/GrpcTimeoutSimulator.Benchmark -- \
    --external-server http://192.168.1.100:5000
```

### 完整参数列表

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `--mode` | auto | 测试模式：auto 或 manual |
| `--concurrency` | 50,100,200,500 | 手动模式并发级别 |
| `--external-server` | - | 外部服务端地址 |
| `--optimize-config` | false | 启用配置优化 |
| `--success-rate` | 0.999 | SLA 成功率阈值 |
| `--p99-threshold` | 200 | SLA P99 延迟阈值(ms) |
| `--warmup-duration` | 5 | 预热时长(秒) |
| `--test-duration` | 10 | 测试时长(秒) |
| `--stability-duration` | 30 | 稳定性验证时长(秒) |
| `--port` | 5000 | 内嵌服务端端口 |
| `--channel-pool-size` | 4 | Channel 池大小 |
| `--request-timeout` | 5000 | 请求超时(ms) |

## 输出报告示例

```
╔══════════════════════════════════════════════════════════╗
║         gRPC 并发能力探测与性能评估                        ║
╚══════════════════════════════════════════════════════════╝

  模式: 内嵌服务端 (http://localhost:5000)
  SLA: 成功率 >= 99.9%, P99 <= 200ms

>>> 启动内嵌服务端
    服务端已启动: http://localhost:5000

>>> 预热阶段 (10 并发 × 5秒)
    完成，建立连接池

>>> 指数增长阶段
    [  20 并发] 成功率: 100.0% | P99:   45ms | 吞吐:  420 req/s  ✓
    [  40 并发] 成功率: 100.0% | P99:   78ms | 吞吐:  512 req/s  ✓
    [  80 并发] 成功率: 100.0% | P99:  156ms | 吞吐:  598 req/s  ✓
    [ 160 并发] 成功率:  99.8% | P99:  278ms | 吞吐:  689 req/s  ✗

>>> 二分搜索阶段
    [ 120 并发] 成功率:  99.9% | P99:  198ms | 吞吐:  645 req/s  ✓
    [ 140 并发] 成功率:  99.9% | P99:  234ms | 吞吐:  667 req/s  ✗
    [ 130 并发] 成功率:  99.9% | P99:  195ms | 吞吐:  656 req/s  ✓

>>> 稳定性验证 (130 并发 × 30秒)
    结果: 成功率 99.92%, P99 稳定在 188-198ms

══════════════════════════════════════════════════════════════
                       环境并发能力评估
══════════════════════════════════════════════════════════════

  ┌─────────────────────────────────────────────────────────┐
  │ 最大并发能力:      130 (成功率 99.9%+)                   │
  │ 有效并发能力:      130 (满足 P99 < 200ms)               │
  │ 饱和吞吐量:        656 req/s                            │
  │                                                         │
  │ ★ 建议生产环境并发上限: 104 (80% 水位)                  │
  └─────────────────────────────────────────────────────────┘

  优化建议:
    1. 当前瓶颈: 队列等待时间占比 60%，单线程处理器是主要限制
    2. 建议增加处理线程或使用多队列分片以提高并发能力
```

## 技术实现要点

### 1. 内嵌服务端

使用 `WebApplication.CreateBuilder()` 创建独立的 ASP.NET Core 应用，复用 Server 项目的服务实现：

```csharp
var app = builder.Build();
app.MapGrpcService<SimulationService>();
await app.StartAsync();
```

### 2. 延迟分布计算

使用百分位数计算：

```csharp
var sorted = latencies.OrderBy(x => x).ToList();
var p99Index = (int)Math.Ceiling(0.99 * sorted.Count) - 1;
var p99 = sorted[Math.Max(0, p99Index)];
```

### 3. 资源快照

从服务端收集诊断信息：
- 峰值队列深度
- 最大队列等待时间
- GC 统计
- 线程池状态

### 4. 取消处理

支持 Ctrl+C 优雅停止：

```csharp
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    cts.Cancel();
};
```

## 扩展点

1. **报告格式**：可扩展 JSON/HTML 报告输出
2. **指标收集**：可集成 Prometheus/Grafana
3. **分布式测试**：支持多客户端协调测试
4. **自定义 SLA**：支持更复杂的 SLA 规则
