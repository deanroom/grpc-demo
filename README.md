# gRPC Timeout Simulator

gRPC 超时模拟与性能评估工具集，用于研究 gRPC 在高并发场景下的超时行为，并提供并发能力探测和性能基准测试功能。

## 项目结构

```
src/
├── GrpcTimeoutSimulator.Proto/           # Protobuf 定义
├── GrpcTimeoutSimulator.Server/          # gRPC 服务端
├── GrpcTimeoutSimulator.Client/          # gRPC 客户端
└── GrpcTimeoutSimulator.Benchmark/       # 并发能力探测与性能基准测试
```

## 快速开始

### 运行基准测试（推荐）

单命令启动内嵌服务端并运行并发探测：

```bash
dotnet run --project src/GrpcTimeoutSimulator.Benchmark
```

### 手动运行服务端和客户端

```bash
# 终端 1: 启动服务端
dotnet run --project src/GrpcTimeoutSimulator.Server

# 终端 2: 启动客户端
dotnet run --project src/GrpcTimeoutSimulator.Client
```

## 基准测试工具

### 功能特性

- **自动并发探测**：使用自适应二分搜索算法自动探测最大并发能力
- **SLA 评估**：基于成功率和 P99 延迟判定是否满足 SLA
- **超时原因分析**：区分 HTTP/2 连接层超时和服务端应用层超时
- **配置优化**：自动探索最佳配置参数

### 核心指标

| 指标 | 定义 | 意义 |
|------|------|------|
| 最大并发数 | 保持成功率 ≥ 99.9% 时的最大并发请求数 | 系统极限 |
| 有效并发数 | 同时满足 P99 延迟阈值的最大并发数 | 实际可用并发 |
| 饱和吞吐量 | 有效并发时的请求/秒 | 容量规划依据 |
| 建议并发上限 | 有效并发数 × 0.8 | 生产环境安全水位 |

### 命令行参数

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

### 参数列表

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

## 架构设计

### 进程内集成模式

Benchmark 工具将 gRPC 服务端和客户端集成到同一进程中：

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

### 并发探测算法

采用自适应二分搜索：

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

## 关键配置说明

### 客户端配置

- **EnableMultipleHttp2Connections**: 启用多 HTTP/2 连接（关键配置）
- **ChannelPoolSize**: Channel 池大小，建议 >= 4
- **MaxConnectionsPerServer**: 每服务器最大连接数

### 服务端配置

- **MaxStreamsPerConnection**: 每连接最大流数，建议 >= 500
- **ThreadPool.SetMinThreads**: 预热线程池，建议 (200, 200)

## 文档

- [设计文档](docs/designs/grpc-benchmark-design.md)
- [验证报告](docs/reports/grpc-timeout-verification-report.md)

## 技术栈

- .NET 9.0
- gRPC / Grpc.Net
- ASP.NET Core Kestrel
- System.CommandLine
