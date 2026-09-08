using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Control.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Control.Services;

public sealed class OperationCache(
    IDistributedCache cache,
    IOptions<Model.Options.CacheOptions> options,
    ControlInstance instance,
    IConnectionMultiplexer? redis = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim localLock = new(1, 1);
    private readonly TimeSpan retention = TimeSpan.FromHours(Math.Clamp(options.Value.OperationRetentionHours, 24, 720));
    private readonly string prefix = $"{options.Value.InstanceName}:";

    public async Task<OperationReservation> ReserveAsync(
        string node,
        string type,
        string idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        string normalizedNode = node.ToLowerInvariant();
        ControlOperation candidate = new(Guid.NewGuid().ToString("N"), idempotencyKey, fingerprint, type, normalizedNode, instance.Id);
        OperationCacheEntry operationEntry = OperationCacheEntry.From(candidate);
        IdempotencyCacheEntry indexEntry = new(candidate.Id, fingerprint, operationEntry);
        string operationJson = JsonSerializer.Serialize(operationEntry, JsonOptions);
        string indexJson = JsonSerializer.Serialize(indexEntry, JsonOptions);
        string indexKey = IdempotencyKey(normalizedNode, type, idempotencyKey);

        if (redis is not null)
        {
            IDatabase database = redis.GetDatabase();
            bool created = await database.StringSetAsync(indexKey, indexJson, retention, When.NotExists);
            if (created)
            {
                try
                {
                    await database.StringSetAsync(OperationKey(candidate.Id), operationJson, retention);
                    return new OperationReservation(OperationEnqueueResult.Created, candidate);
                }
                catch
                {
                    await ReleaseAsync(candidate, CancellationToken.None);
                    throw;
                }
            }

            RedisValue value = await database.StringGetAsync(indexKey);
            if (value.IsNull)
            {
                return await ReserveAsync(node, type, idempotencyKey, fingerprint, cancellationToken);
            }
            IdempotencyCacheEntry existing = DeserializeIndex(value.ToString());
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new OperationReservation(OperationEnqueueResult.Conflict, null);
            }
            ControlOperation operation = await GetAsync(existing.OperationId, cancellationToken) ?? existing.Operation.ToOperation();
            return new OperationReservation(OperationEnqueueResult.Existing, operation);
        }

        await localLock.WaitAsync(cancellationToken);
        try
        {
            string? cachedIndex = await cache.GetStringAsync(indexKey, cancellationToken);
            if (cachedIndex is not null)
            {
                IdempotencyCacheEntry existing = DeserializeIndex(cachedIndex);
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new OperationReservation(OperationEnqueueResult.Conflict, null);
                }
                ControlOperation operation = await GetAsync(existing.OperationId, cancellationToken) ?? existing.Operation.ToOperation();
                return new OperationReservation(OperationEnqueueResult.Existing, operation);
            }

            DistributedCacheEntryOptions cacheOptions = CacheOptions();
            await cache.SetStringAsync(OperationKey(candidate.Id), operationJson, cacheOptions, cancellationToken);
            await cache.SetStringAsync(indexKey, indexJson, cacheOptions, cancellationToken);
            return new OperationReservation(OperationEnqueueResult.Created, candidate);
        }
        finally
        {
            localLock.Release();
        }
    }

    public async Task SetAsync(ControlOperation operation, CancellationToken cancellationToken = default)
    {
        OperationCacheEntry entry = OperationCacheEntry.From(operation);
        string operationJson = JsonSerializer.Serialize(entry, JsonOptions);
        string indexKey = IdempotencyKey(operation.Node.ToLowerInvariant(), operation.Type, operation.IdempotencyKey);
        if (redis is not null)
        {
            IDatabase database = redis.GetDatabase();
            await database.StringSetAsync(OperationKey(operation.Id), operationJson, retention);
            RedisValue current = await database.StringGetAsync(indexKey);
            if (!current.IsNull)
            {
                IdempotencyCacheEntry index = DeserializeIndex(current.ToString());
                if (string.Equals(index.OperationId, operation.Id, StringComparison.Ordinal))
                {
                    await database.StringSetAsync(indexKey, JsonSerializer.Serialize(index with { Operation = entry }, JsonOptions), retention);
                }
            }
            return;
        }

        await cache.SetStringAsync(OperationKey(operation.Id), operationJson, CacheOptions(), cancellationToken);
        string? cachedIndex = await cache.GetStringAsync(indexKey, cancellationToken);
        if (cachedIndex is not null)
        {
            IdempotencyCacheEntry index = DeserializeIndex(cachedIndex);
            if (string.Equals(index.OperationId, operation.Id, StringComparison.Ordinal))
            {
                await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(index with { Operation = entry }, JsonOptions), CacheOptions(), cancellationToken);
            }
        }
    }

    public async Task<ControlOperation?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        string? json;
        if (redis is not null)
        {
            RedisValue value = await redis.GetDatabase().StringGetAsync(OperationKey(id));
            json = value.IsNull ? null : value.ToString();
        }
        else
        {
            json = await cache.GetStringAsync(OperationKey(id), cancellationToken);
        }
        return json is null ? null : JsonSerializer.Deserialize<OperationCacheEntry>(json, JsonOptions)?.ToOperation();
    }

    public async Task<bool> IsOwnerAliveAsync(string ownerId)
    {
        if (redis is null || string.Equals(ownerId, instance.Id, StringComparison.Ordinal))
        {
            return true;
        }
        return await redis.GetDatabase().KeyExistsAsync(OwnerKey(ownerId));
    }

    public async Task RefreshOwnerAsync()
    {
        if (redis is not null)
        {
            await redis.GetDatabase().StringSetAsync(OwnerKey(instance.Id), "1", TimeSpan.FromSeconds(30));
        }
    }

    public async Task ReleaseAsync(ControlOperation operation, CancellationToken cancellationToken = default)
    {
        string indexKey = IdempotencyKey(operation.Node.ToLowerInvariant(), operation.Type, operation.IdempotencyKey);
        if (redis is not null)
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                "local current=redis.call('GET',KEYS[1]); if current then local item=cjson.decode(current); if item.operationId==ARGV[1] then redis.call('DEL',KEYS[1]); return 1 end end; return 0",
                [indexKey],
                [operation.Id]);
            await redis.GetDatabase().KeyDeleteAsync(OperationKey(operation.Id));
            return;
        }

        await localLock.WaitAsync(cancellationToken);
        try
        {
            await cache.RemoveAsync(indexKey, cancellationToken);
            await cache.RemoveAsync(OperationKey(operation.Id), cancellationToken);
        }
        finally
        {
            localLock.Release();
        }
    }

    private DistributedCacheEntryOptions CacheOptions() => new() { AbsoluteExpirationRelativeToNow = retention };
    private string OperationKey(string id) => $"{prefix}operation:{id}";
    private string IdempotencyKey(string node, string type, string key)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{node}\n{type}\n{key}"))).ToLowerInvariant();
        return $"{prefix}idempotency:{hash}";
    }
    private string OwnerKey(string ownerId) => $"{prefix}owner:{ownerId}";
    private static IdempotencyCacheEntry DeserializeIndex(string json) => JsonSerializer.Deserialize<IdempotencyCacheEntry>(json, JsonOptions)!;
}
