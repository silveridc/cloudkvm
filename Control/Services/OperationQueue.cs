using Control.Model;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Control.Services;

public sealed class OperationQueue(OperationCache operationCache)
{
    private readonly ConcurrentDictionary<string, ControlOperation> localOperations = new(StringComparer.Ordinal);
    private readonly Channel<OperationWorkItem> queue = Channel.CreateBounded<OperationWorkItem>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });

    public async Task<OperationReservation> EnqueueAsync(
        string idempotencyKey,
        string fingerprint,
        string type,
        string node,
        Func<CancellationToken, Task<object?>> work,
        CancellationToken cancellationToken)
    {
        OperationReservation reservation = await operationCache.ReserveAsync(
            node,
            type,
            idempotencyKey,
            fingerprint,
            cancellationToken);
        if (reservation.Result != OperationEnqueueResult.Created || reservation.Operation is null)
        {
            return reservation;
        }

        ControlOperation operation = reservation.Operation;
        localOperations[operation.Id] = operation;
        if (queue.Writer.TryWrite(new OperationWorkItem(operation, work)))
        {
            return reservation;
        }

        localOperations.TryRemove(operation.Id, out _);
        operation.Completion.TrySetResult();
        await operationCache.ReleaseAsync(operation, CancellationToken.None);
        return new OperationReservation(OperationEnqueueResult.QueueFull, null);
    }

    public async Task<ControlOperation?> GetAsync(string id, CancellationToken cancellationToken)
    {
        return localOperations.TryGetValue(id, out ControlOperation? local)
            ? local
            : await operationCache.GetAsync(id, cancellationToken);
    }

    public IAsyncEnumerable<OperationWorkItem> ReadAllAsync(CancellationToken cancellationToken) => queue.Reader.ReadAllAsync(cancellationToken);

    public async Task<bool> IsOwnerAliveAsync(string ownerId) => await operationCache.IsOwnerAliveAsync(ownerId);

    public async Task SaveAsync(ControlOperation operation, CancellationToken cancellationToken = default)
    {
        await operationCache.SetAsync(operation, cancellationToken);
        if (operation.CompletedAt is not null)
        {
            localOperations.TryRemove(operation.Id, out _);
        }
    }
}
