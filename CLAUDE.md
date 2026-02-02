# Claude Code 项目指南

## 项目概述

这是一个 gRPC 超时模拟与性能评估工具集，用于研究 gRPC 在高并发场景下的超时行为。

## 项目结构

```
src/
├── GrpcTimeoutSimulator.Proto/           # Protobuf 定义
│   └── simulation.proto
└── GrpcTimeoutSimulator.Benchmark/       # 核心工具（包含内嵌服务端）
    ├── Program.cs                        # 命令行入口
    ├── Hosting/EmbeddedServer.cs         # 内嵌服务端启动器
    ├── Benchmarks/
    │   ├── BenchmarkConfig.cs            # 配置模型
    │   ├── BenchmarkResult.cs            # 结果模型
    │   ├── BenchmarkRunner.cs            # 测试运行器
    │   ├── ConcurrencyProber.cs          # 并发探测器（二分搜索算法）
    │   └── SteadyStateLoadGenerator.cs   # 稳态负载生成器
    ├── Server/
    │   ├── Services/SimulationService.cs # gRPC 服务实现
    │   └── Processing/SingleThreadProcessor.cs  # 单线程处理器
    └── Reporting/ConsoleReporter.cs      # 控制台报告
```

## 关键技术概念

### HTTP/2 连接层瓶颈

gRPC 使用 HTTP/2，单个连接默认限制约 100 个并发流（SETTINGS_MAX_CONCURRENT_STREAMS）。高并发场景需要：

1. 启用 `EnableMultipleHttp2Connections = true`
2. 使用 Channel 池分散负载
3. 预热线程池避免线程饥饿

### SLA 判定

- 成功率阈值（默认 99.9%）
- P99 延迟阈值（默认 500ms）

## 常用命令

```bash
# 运行基准测试
dotnet run --project src/GrpcTimeoutSimulator.Benchmark

# 自定义 P99 阈值
dotnet run --project src/GrpcTimeoutSimulator.Benchmark -- --p99-threshold 400

# 手动模式测试固定并发
dotnet run --project src/GrpcTimeoutSimulator.Benchmark -- --mode manual --concurrency 50,100,200

# 构建项目
dotnet build
```

## 代码风格

- 使用中文 XML 文档注释
- 类名和方法名使用英文
- 控制台输出使用中文

## 重要文件

- `docs/designs/grpc-benchmark-design.md` - 详细设计文档
- `docs/reports/grpc-timeout-verification-report.md` - 验证报告
- `docs/plans/2026-01-30-grpc-timeout-simulator-design.md` - 初始设计计划

## 修改指南

### 添加新的配置参数

1. 在 `BenchmarkConfig.cs` 中添加属性
2. 在 `Program.cs` 中添加命令行参数
3. 在相应组件中使用新配置

### 修改探测算法

核心逻辑在 `ConcurrencyProber.cs`：
- `ExponentialGrowthPhaseAsync` - 指数增长阶段
- `BinarySearchPhaseAsync` - 二分搜索阶段
- `VerifyStabilityAsync` - 稳定性验证

## 注意事项

1. 服务端代码集成在 `Benchmark/Server/` 目录下，`EmbeddedServer` 通过 DI 容器启动
2. `EmbeddedServer` 通过 DI 容器管理生命周期，不要手动 Dispose 服务
3. 测试高并发时注意调整 `--p99-threshold` 参数（默认 500ms）
