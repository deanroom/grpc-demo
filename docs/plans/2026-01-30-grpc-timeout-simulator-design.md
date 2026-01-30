# gRPC 超时仿真环境设计文档

## 概述

创建一个 .NET gRPC 仿真应用，用于验证以下场景是否会导致 Deadline Exceeded：
- 客户端突发高并发（1ms 内数十到上百个请求）
- 服务端 BlockingCollection 单线程队列处理
- 处理时间从微秒到几十毫秒不等
- 3 秒超时（Deadline）

**核心目标**：不仅要复现超时问题，还要精确诊断每次超时的具体原因。

## 项目结构

```
grpc-demo/
├── GrpcTimeoutSimulator.sln
├── docs/
│   └── plans/
│       └── 2026-01-30-grpc-timeout-simulator-design.md
├── src/
│   ├── GrpcTimeoutSimulator.Proto/
│   │   ├── simulation.proto
│   │   └── GrpcTimeoutSimulator.Proto.csproj
│   ├── GrpcTimeoutSimulator.Server/
│   │   ├── Services/SimulationService.cs
│   │   ├── Processing/SingleThreadProcessor.cs
│   │   ├── Diagnostics/TimeoutDiagnostics.cs
│   │   └── Program.cs
│   └── GrpcTimeoutSimulator.Client/
│       ├── LoadGenerators/BurstLoadGenerator.cs
│       ├── Diagnostics/ClientDiagnostics.cs
│       └── Program.cs
```

## 核心设计

### 1. 诊断时间点埋点

每个请求记录以下时间戳：

| 时间点 | 位置 | 描述 |
|--------|------|------|
| T1 | 客户端 | 发起请求时间 |
| T2 | 服务端 | 请求到达服务端时间 |
| T3 | 服务端 | 进入 BlockingCollection 队列时间 |
| T4 | 服务端 | 从队列取出开始处理时间 |
| T5 | 服务端 | 处理完成时间 |
| T6 | 客户端 | 响应到达客户端时间 |

### 2. 超时原因判定逻辑

| 时间段 | 计算方式 | 原因判定 |
|--------|----------|----------|
| 网络/gRPC 开销 | T2-T1, T6-T5 | 网络延迟或 gRPC 框架处理 |
| 队列等待时间 | T4-T3 | **队列排队等待过长** |
| 处理时间 | T5-T4 | **单次处理耗时过长** |
| 线程池延迟 | 异步任务调度时间 | **ThreadPool 饱和** |
| GC 暂停 | GC 通知事件 | **GC 导致暂停** |

判定规则：
1. 如果 (T4-T3) > 50% 总耗时 → "队列等待过长"
2. 如果 (T5-T4) > 50% 总耗时 → "处理时间过长"
3. 如果期间有 GC 事件且 GC 耗时 > 100ms → "GC 暂停"
4. 如果 ThreadPool 可用线程 < 5 → "线程池饱和"
5. 原因可叠加

### 3. 负载生成器配置

```csharp
public class LoadConfig
{
    // 突发配置
    public int BurstSize { get; set; } = 100;        // 每次突发的请求数
    public int BurstIntervalMs { get; set; } = 1;    // 突发内请求间隔（ms）
    public int BurstCount { get; set; } = 10;        // 突发次数
    public int BurstGapMs { get; set; } = 500;       // 突发之间的间隔（ms）

    // 请求配置
    public int DeadlineMs { get; set; } = 3000;      // 超时时间
    public bool UseSyncCalls { get; set; } = true;   // 是否使用同步调用

    // 服务端处理时间模拟
    public int MinProcessingTimeUs { get; set; } = 10;    // 最小处理时间（微秒）
    public int MaxProcessingTimeMs { get; set; } = 50;    // 最大处理时间（毫秒）
}
```

### 4. 输出格式

#### 实时日志（控制台）
```
[12:00:00.123] REQ-0042 TIMEOUT (3002ms)
  原因: 队列等待过长 (2850ms)
  详情: 队列深度=87, 处理时间=120ms, GC=无

[12:00:00.456] REQ-0089 TIMEOUT (3105ms)
  原因: GC暂停 + 队列等待
  详情: GC暂停=450ms, 队列等待=2200ms, 处理时间=35ms
```

#### 汇总报告
```
======== 仿真运行报告 ========
总请求数: 1000
成功: 850 (85%)
超时: 150 (15%)

超时原因分布:
  队列等待过长:     92 (61.3%)
  处理时间过长:     28 (18.7%)
  线程池饱和:       18 (12.0%)
  GC暂停:          12 (8.0%)

关键指标:
  队列峰值深度: 156
  最长队列等待: 2850ms
  GC 次数: 3 (Gen0: 2, Gen2: 1)
  线程池最低可用: 2
```

## 实施步骤

### 步骤 1：创建解决方案和项目结构
- 创建 `GrpcTimeoutSimulator.sln`
- 创建三个项目：Proto、Server、Client
- 配置项目引用和 NuGet 包

### 步骤 2：定义 Proto 服务
- 创建 `simulation.proto`
- 定义服务、请求、响应消息

### 步骤 3：实现服务端
- TimeoutDiagnostics：GC 监控、ThreadPool 监控
- SingleThreadProcessor：BlockingCollection 单线程队列
- SimulationService：gRPC 服务实现

### 步骤 4：实现客户端
- BurstLoadGenerator：可配置的突发负载生成器
- ClientDiagnostics：超时原因分析和报告生成
- Program：命令行入口

### 步骤 5：验证测试
- 构建并运行
- 调整参数验证不同场景

## 验证方法

1. **构建**
   ```bash
   dotnet build GrpcTimeoutSimulator.sln
   ```

2. **运行**
   ```bash
   # 终端 1：启动服务端
   dotnet run --project src/GrpcTimeoutSimulator.Server

   # 终端 2：运行客户端
   dotnet run --project src/GrpcTimeoutSimulator.Client -- --burst-size 100 --deadline 3000
   ```

3. **验证不同场景**
   - 增大 `BurstSize` → 更多队列等待超时
   - 增大 `MaxProcessingTimeMs` → 更多处理时间超时
   - 减小 `BurstGapMs` → 更容易触发线程池饱和
