using Control.Model;
using Control.Model.Response;
using Grpc.Core;

namespace Control.Services;

public sealed class OperationWorker(OperationQueue operationQueue, ILogger<OperationWorker> logger) : BackgroundService
{
    private const int WorkerCount = 4;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.WhenAll(Enumerable.Range(0, WorkerCount).Select(_ => RunWorkerAsync(stoppingToken)));
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (OperationWorkItem item in operationQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ExecuteOperationAsync(item, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "Operation worker failed while processing {OperationId}.", item.Operation.Id);
            }
        }
    }

    private async Task ExecuteOperationAsync(OperationWorkItem item, CancellationToken stoppingToken)
    {
        ControlOperation operation = item.Operation;
        operation.Status = OperationStatus.Running;
        await SaveSafelyAsync(operation);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            operation.Result = await item.Work(timeout.Token);
            operation.Status = OperationStatus.Succeeded;
        }
        catch (RpcException exception)
        {
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = exception.StatusCode.ToString();
            operation.Error = SanitizeRpcError(exception.StatusCode);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = StatusCode.Cancelled.ToString();
            operation.Error = "Operation was cancelled because the service is stopping.";
        }
        catch (OperationCanceledException)
        {
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = StatusCode.DeadlineExceeded.ToString();
            operation.Error = "Operation exceeded the 30 minute execution limit.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Operation {OperationId} failed.", operation.Id);
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = StatusCode.Internal.ToString();
            operation.Error = "The operation failed on the cluster node.";
        }
        finally
        {
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Completion.TrySetResult();
            await SaveSafelyAsync(operation);
        }
    }

    private async Task SaveSafelyAsync(ControlOperation operation)
    {
        try
        {
            await operationQueue.SaveAsync(operation, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist operation {OperationId}.", operation.Id);
        }
    }

    private static string SanitizeRpcError(StatusCode statusCode)
    {
        return statusCode switch
        {
            StatusCode.InvalidArgument => "The cluster rejected the request parameters.",
            StatusCode.NotFound => "The requested cluster resource was not found.",
            StatusCode.FailedPrecondition => "The cluster resource is not in the required state.",
            StatusCode.Unauthenticated => "Cluster authentication failed.",
            StatusCode.Unavailable => "The cluster node is unavailable.",
            _ => "The operation failed on the cluster node."
        };
    }
}
