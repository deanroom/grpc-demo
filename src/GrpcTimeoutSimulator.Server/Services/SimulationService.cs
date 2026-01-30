using Grpc.Core;
using GrpcTimeoutSimulator.Proto;
using GrpcTimeoutSimulator.Server.Diagnostics;
using GrpcTimeoutSimulator.Server.Processing;

namespace GrpcTimeoutSimulator.Server.Services;

public class SimulationService : Proto.SimulationService.SimulationServiceBase
{
    private readonly SingleThreadProcessor _processor;
    private readonly TimeoutDiagnostics _diagnostics;
    private readonly ILogger<SimulationService> _logger;

    public SimulationService(
        SingleThreadProcessor processor,
        TimeoutDiagnostics diagnostics,
        ILogger<SimulationService> logger)
    {
        _processor = processor;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public override async Task<ProcessResponse> Process(ProcessRequest request, ServerCallContext context)
    {
        // T2: 记录到达时间
        var timeline = new RequestTimeline
        {
            RequestId = request.RequestId,
            ClientSendTimeTicks = request.ClientSendTimeTicks,
            ArrivalTimeTicks = DateTime.UtcNow.Ticks
        };

        var tcs = new TaskCompletionSource<bool>();
        var workItem = new WorkItem
        {
            Timeline = timeline,
            CompletionSource = tcs,
            CancellationToken = context.CancellationToken
        };

        // 入队
        _processor.Enqueue(workItem);

        try
        {
            // 等待处理完成
            await tcs.Task;

            return new ProcessResponse
            {
                RequestId = request.RequestId,
                Success = true,
                QueueDepthAtEnqueue = timeline.QueueDepthAtEnqueue,
                Timeline = new ServerTimeline
                {
                    ArrivalTimeTicks = timeline.ArrivalTimeTicks,
                    EnqueueTimeTicks = timeline.EnqueueTimeTicks,
                    DequeueTimeTicks = timeline.DequeueTimeTicks,
                    CompleteTimeTicks = timeline.CompleteTimeTicks
                },
                DiagnosticInfo = new DiagnosticInfo
                {
                    GcOccurred = timeline.GcOccurred,
                    GcGeneration = timeline.GcGeneration,
                    GcDurationMs = timeline.GcDurationMs,
                    AvailableWorkerThreads = timeline.AvailableWorkerThreads,
                    AvailableIoThreads = timeline.AvailableIoThreads,
                    ProcessingTimeUs = timeline.ProcessingTimeUs
                }
            };
        }
        catch (OperationCanceledException)
        {
            // 计算已经花费的时间
            var now = DateTime.UtcNow.Ticks;
            var totalTimeMs = (now - timeline.ArrivalTimeTicks) / (double)TimeSpan.TicksPerMillisecond;
            var queueWaitMs = timeline.DequeueTimeTicks > 0
                ? (timeline.DequeueTimeTicks - timeline.EnqueueTimeTicks) / (double)TimeSpan.TicksPerMillisecond
                : (now - timeline.EnqueueTimeTicks) / (double)TimeSpan.TicksPerMillisecond;

            // 判断超时原因
            string reason;
            if (timeline.DequeueTimeTicks == 0)
            {
                reason = "队列等待过长（仍在队列中）";
            }
            else if (queueWaitMs > totalTimeMs * 0.5)
            {
                reason = "队列等待过长";
            }
            else
            {
                reason = "处理时间过长";
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[服务端] {request.RequestId} 超时 - 原因: {reason}, 队列等待={queueWaitMs:F0}ms, 队列深度={timeline.QueueDepthAtEnqueue}");
            Console.ResetColor();

            throw new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled"));
        }
    }
}
