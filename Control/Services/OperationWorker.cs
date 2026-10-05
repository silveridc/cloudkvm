using Control.Model;
using Control.Model.Response;
using Grpc.Core;

namespace Control.Services;

/// <summary>后台操作执行器：消费队列工作项，带执行超时并把终态持久化。</summary>
public sealed class OperationWorker(
    OperationQueue operationQueue,
    IOptions<Model.Options.OperationOptions> options,
    ILogger<OperationWorker> logger) : BackgroundService
{
    private readonly int _workerCount = Math.Clamp(options.Value.WorkerCount, 1, 64);
    private readonly TimeSpan _executionTimeout = TimeSpan.FromMinutes(Math.Clamp(options.Value.ExecutionTimeoutMinutes, 1, 1440));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.WhenAll(Enumerable.Range(0, _workerCount).Select(_ => RunWorkerAsync(stoppingToken)));
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
        if (!await SaveSafelyAsync(operation))
        {
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = StatusCode.Unavailable.ToString();
            operation.Error = "Operation state storage is unavailable.";
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Completion.TrySetResult();
            await SaveSafelyAsync(operation);
            operationQueue.ReleaseQueueSlot();
            return;
        }
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(_executionTimeout);
            operation.Result = await item.Work(timeout.Token);
            operation.Status = OperationStatus.Succeeded;
        }
        catch (RpcException exception)
        {
            logger.LogWarning(exception, "Cluster RPC failed while executing operation {OperationId}.", operation.Id);
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = exception.StatusCode.ToString();
            operation.Error = "The cluster operation could not be completed.";
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
            operation.Error = $"Operation exceeded the {_executionTimeout.TotalMinutes:0} minute execution limit.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Operation {OperationId} failed.", operation.Id);
            operation.Status = OperationStatus.Failed;
            operation.GrpcStatusCode = StatusCode.Internal.ToString();
            operation.Error = "The operation failed.";
        }
        finally
        {
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Completion.TrySetResult();
            await SaveSafelyAsync(operation);
            operationQueue.ReleaseQueueSlot();
        }
    }

    private async Task<bool> SaveSafelyAsync(ControlOperation operation)
    {
        try
        {
            await operationQueue.SaveAsync(operation, CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist operation {OperationId}.", operation.Id);
            return false;
        }
    }
}
