using Grpc.Core;
using GrpcTimeoutSimulator.Proto;
using GrpcTimeoutSimulator.Benchmark.Server.Processing;

namespace GrpcTimeoutSimulator.Benchmark.Server.Services;

public class SimulationService : Proto.SimulationService.SimulationServiceBase
{
    private readonly SingleThreadProcessor _processor;

    public SimulationService(SingleThreadProcessor processor)
    {
        _processor = processor;
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
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled"));
        }
    }
}
