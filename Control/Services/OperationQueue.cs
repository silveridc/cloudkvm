using Control.Model;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Control.Services;

/// <summary>异步操作队列：先查幂等预留再入有界通道，容量满时返回 QueueFull。</summary>
public sealed class OperationQueue
{
    private readonly OperationCache _operationCache;
    private readonly ConcurrentDictionary<string, ControlOperation> _localOperations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueSlots;
    private readonly Channel<OperationWorkItem> _queue;

    public OperationQueue(OperationCache operationCache, IOptions<Model.Options.OperationOptions> options)
    {
        this._operationCache = operationCache;
        int capacity = Math.Clamp(options.Value.QueueCapacity, 1, 4096);
        _queueSlots = new SemaphoreSlim(capacity, capacity);
        _queue = Channel.CreateBounded<OperationWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public async Task<OperationReservation> EnqueueAsync(
        string idempotencyKey,
        string fingerprint,
        string type,
        string node,
        Func<CancellationToken, Task<object?>> work,
        CancellationToken cancellationToken)
    {
        OperationReservation? existing = await _operationCache.LookupAsync(node, type, idempotencyKey, fingerprint, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }
        if (!await _queueSlots.WaitAsync(0, cancellationToken))
        {
            return new OperationReservation(OperationEnqueueResult.QueueFull, null);
        }

        OperationReservation reservation;
        try
        {
            reservation = await _operationCache.ReserveAsync(
                node,
                type,
                idempotencyKey,
                fingerprint,
                cancellationToken);
        }
        catch
        {
            _queueSlots.Release();
            throw;
        }
        if (reservation.Result != OperationEnqueueResult.Created || reservation.Operation is null)
        {
            _queueSlots.Release();
            return reservation;
        }

        ControlOperation operation = reservation.Operation;
        _localOperations[operation.Id] = operation;
        if (_queue.Writer.TryWrite(new OperationWorkItem(operation, work)))
        {
            return reservation;
        }

        _localOperations.TryRemove(operation.Id, out _);
        try
        {
            await _operationCache.ReleaseAsync(operation, CancellationToken.None);
        }
        finally
        {
            _queueSlots.Release();
        }
        return new OperationReservation(OperationEnqueueResult.QueueFull, null);
    }

    public async Task<ControlOperation?> GetAsync(string id, CancellationToken cancellationToken)
    {
        return _localOperations.TryGetValue(id, out ControlOperation? local)
            ? local
            : await _operationCache.GetAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<ControlOperation>> ListAsync(
        string node,
        int limit,
        DateTimeOffset? before,
        CancellationToken cancellationToken = default)
    {
        return _operationCache.ListAsync(node, limit, before, cancellationToken);
    }

    public IAsyncEnumerable<OperationWorkItem> ReadAllAsync(CancellationToken cancellationToken) => _queue.Reader.ReadAllAsync(cancellationToken);

    public void ReleaseQueueSlot() => _queueSlots.Release();

    public async Task<bool> IsOwnerAliveAsync(string ownerId) => await _operationCache.IsOwnerAliveAsync(ownerId);

    public async Task SaveAsync(ControlOperation operation, CancellationToken cancellationToken = default)
    {
        await _operationCache.SetAsync(operation, cancellationToken);
        if (operation.CompletedAt is not null)
        {
            _localOperations.TryRemove(operation.Id, out _);
        }
    }
}
