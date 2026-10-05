using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Control.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Control.Services;

/// <summary>异步操作的分布式缓存：幂等预留与状态存取；Redis 原子脚本保证唯一，无 Redis 时退回本地锁。</summary>
public sealed class OperationCache(
    IDistributedCache cache,
    IOptions<Model.Options.CacheOptions> options,
    ControlInstance instance,
    IConnectionMultiplexer? redis = null)
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _localLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _localOperationsByNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _retention = TimeSpan.FromHours(Math.Clamp(options.Value.OperationRetentionHours, 168, 720));
    private readonly string _prefix = $"{options.Value.InstanceName}:";

    public async Task<OperationReservation?> LookupAsync(
        string node,
        string type,
        string idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        string normalizedNode = node.ToLowerInvariant();
        string reservationHash = CreateReservationHash(normalizedNode, type, idempotencyKey);
        string? json;
        if (redis is not null)
        {
            RedisValue value = await redis.GetDatabase().StringGetAsync(IdempotencyKey(reservationHash));
            json = value.IsNull ? null : value.ToString();
        }
        else
        {
            json = await cache.GetStringAsync(IdempotencyKey(reservationHash), cancellationToken);
        }
        if (json is null)
        {
            return null;
        }

        IdempotencyCacheEntry existing = DeserializeIndex(json);
        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new OperationReservation(OperationEnqueueResult.Conflict, null);
        }
        ControlOperation operation = await GetAsync(existing.OperationId, cancellationToken) ?? existing.Operation.ToOperation();
        return new OperationReservation(OperationEnqueueResult.Existing, operation);
    }

    public async Task<OperationReservation> ReserveAsync(
        string node,
        string type,
        string idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        string normalizedNode = node.ToLowerInvariant();
        string reservationHash = CreateReservationHash(normalizedNode, type, idempotencyKey);
        ControlOperation candidate = new(reservationHash, idempotencyKey, fingerprint, type, normalizedNode, instance.Id);
        OperationCacheEntry operationEntry = OperationCacheEntry.From(candidate);
        IdempotencyCacheEntry indexEntry = new(candidate.Id, fingerprint, operationEntry);
        string operationJson = JsonSerializer.Serialize(operationEntry, _jsonOptions);
        string indexJson = JsonSerializer.Serialize(indexEntry, _jsonOptions);
        string indexKey = IdempotencyKey(reservationHash);
        string operationKey = OperationKey(reservationHash);

        if (redis is not null)
        {
            IDatabase database = redis.GetDatabase();
            RedisResult result = await database.ScriptEvaluateAsync(
                "local current=redis.call('GET',KEYS[1]); if current then return {0,current} end; redis.call('SET',KEYS[2],ARGV[1],'PX',ARGV[3]); redis.call('SET',KEYS[1],ARGV[2],'PX',ARGV[3]); return {1,ARGV[2]}",
                [indexKey, operationKey],
                [operationJson, indexJson, ((long)_retention.TotalMilliseconds).ToString()]);
            // 操作 ID 由确定性哈希生成，只有原子脚本能判定哪个调用者抢占成功。
            RedisResult[] reservation = (RedisResult[])result!;
            bool created = (long)reservation[0] == 1;
            IdempotencyCacheEntry reserved = DeserializeIndex((string)reservation[1]!);
            if (!string.Equals(reserved.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new OperationReservation(OperationEnqueueResult.Conflict, null);
            }
            ControlOperation operation = await GetAsync(reserved.OperationId, cancellationToken) ?? reserved.Operation.ToOperation();
            if (created)
            {
                await AddToIndexAsync(operation);
            }
            return new OperationReservation(
                created
                    ? OperationEnqueueResult.Created
                    : OperationEnqueueResult.Existing,
                operation);
        }

        await _localLock.WaitAsync(cancellationToken);
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
            await cache.SetStringAsync(operationKey, operationJson, cacheOptions, cancellationToken);
            await cache.SetStringAsync(indexKey, indexJson, cacheOptions, cancellationToken);
            await AddToIndexAsync(candidate);
            return new OperationReservation(OperationEnqueueResult.Created, candidate);
        }
        finally
        {
            _localLock.Release();
        }
    }

    public async Task SetAsync(ControlOperation operation, CancellationToken cancellationToken = default)
    {
        OperationCacheEntry entry = OperationCacheEntry.From(operation);
        string operationJson = JsonSerializer.Serialize(entry, _jsonOptions);
        string reservationHash = CreateReservationHash(operation.Node.ToLowerInvariant(), operation.Type, operation.IdempotencyKey);
        string indexKey = IdempotencyKey(reservationHash);
        string operationKey = OperationKey(reservationHash);
        if (redis is not null)
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                "local current=redis.call('GET',KEYS[1]); if current then local item=cjson.decode(current); if item.operationId==ARGV[1] then redis.call('SET',KEYS[2],ARGV[2],'PX',ARGV[3]); redis.call('SET',KEYS[1],ARGV[4],'PX',ARGV[3]); return 1 end end; return 0",
                [indexKey, operationKey],
                [operation.Id, operationJson, ((long)_retention.TotalMilliseconds).ToString(), JsonSerializer.Serialize(new IdempotencyCacheEntry(operation.Id, operation.Fingerprint, entry), _jsonOptions)]);
            await AddToIndexAsync(operation);
            return;
        }

        await cache.SetStringAsync(operationKey, operationJson, CacheOptions(), cancellationToken);
        string? cachedIndex = await cache.GetStringAsync(indexKey, cancellationToken);
        if (cachedIndex is not null)
        {
            IdempotencyCacheEntry index = DeserializeIndex(cachedIndex);
            if (string.Equals(index.OperationId, operation.Id, StringComparison.Ordinal))
            {
                await cache.SetStringAsync(indexKey, JsonSerializer.Serialize(index with { Operation = entry }, _jsonOptions), CacheOptions(), cancellationToken);
            }
        }
        await AddToIndexAsync(operation);
    }

    public async Task<IReadOnlyList<ControlOperation>> ListAsync(
        string node,
        int limit,
        DateTimeOffset? before,
        CancellationToken cancellationToken = default)
    {
        string normalizedNode = node.ToLowerInvariant();
        int effectiveLimit = Math.Clamp(limit, 1, 100);
        if (redis is not null)
        {
            IDatabase database = redis.GetDatabase();
            double maximumScore = before?.ToUnixTimeMilliseconds() ?? double.PositiveInfinity;
            RedisValue[] ids = await database.SortedSetRangeByScoreAsync(
                OperationIndexKey(normalizedNode),
                stop: maximumScore,
                exclude: before is null ? Exclude.None : Exclude.Stop,
                order: Order.Descending,
                take: Math.Min(effectiveLimit * 4, 400));
            List<ControlOperation> operations = [];
            List<RedisValue> stale = [];
            foreach (RedisValue id in ids)
            {
                ControlOperation? operation = await GetAsync(id.ToString(), cancellationToken);
                if (operation is null)
                {
                    stale.Add(id);
                    continue;
                }
                operations.Add(operation);
                if (operations.Count == effectiveLimit)
                {
                    break;
                }
            }
            if (stale.Count > 0)
            {
                await database.SortedSetRemoveAsync(OperationIndexKey(normalizedNode), stale.ToArray());
            }
            return operations;
        }

        if (!_localOperationsByNode.TryGetValue(normalizedNode, out ConcurrentDictionary<string, DateTimeOffset>? index))
        {
            return [];
        }
        List<ControlOperation> local = [];
        foreach ((string id, DateTimeOffset createdAt) in index
                     .Where(item => before is null || item.Value < before.Value)
                     .OrderByDescending(item => item.Value)
                     .Take(effectiveLimit * 4))
        {
            ControlOperation? operation = await GetAsync(id, cancellationToken);
            if (operation is null)
            {
                index.TryRemove(id, out _);
                continue;
            }
            local.Add(operation);
            if (local.Count == effectiveLimit)
            {
                break;
            }
        }
        return local;
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
        return json is null ? null : JsonSerializer.Deserialize<OperationCacheEntry>(json, _jsonOptions)?.ToOperation();
    }

    public async Task<bool> IsOwnerAliveAsync(string ownerId)
    {
        if (string.Equals(ownerId, instance.Id, StringComparison.Ordinal))
        {
            return true;
        }
        if (redis is null)
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
        string reservationHash = CreateReservationHash(operation.Node.ToLowerInvariant(), operation.Type, operation.IdempotencyKey);
        string indexKey = IdempotencyKey(reservationHash);
        string operationKey = OperationKey(reservationHash);
        if (redis is not null)
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                "local current=redis.call('GET',KEYS[1]); if current then local item=cjson.decode(current); if item.operationId==ARGV[1] then redis.call('DEL',KEYS[1]); redis.call('DEL',KEYS[2]); return 1 end end; return 0",
                [indexKey, operationKey],
                [operation.Id]);
            await RemoveFromIndexAsync(operation);
            return;
        }

        await _localLock.WaitAsync(cancellationToken);
        try
        {
            await cache.RemoveAsync(indexKey, cancellationToken);
            await cache.RemoveAsync(operationKey, cancellationToken);
            await RemoveFromIndexAsync(operation);
        }
        finally
        {
            _localLock.Release();
        }
    }

    private async Task AddToIndexAsync(ControlOperation operation)
    {
        string normalizedNode = operation.Node.ToLowerInvariant();
        if (redis is not null)
        {
            IDatabase database = redis.GetDatabase();
            string key = OperationIndexKey(normalizedNode);
            await database.SortedSetAddAsync(key, operation.Id, operation.CreatedAt.ToUnixTimeMilliseconds());
            await database.KeyExpireAsync(key, _retention);
            return;
        }

        ConcurrentDictionary<string, DateTimeOffset> index = _localOperationsByNode.GetOrAdd(
            normalizedNode,
            _ => new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        index[operation.Id] = operation.CreatedAt;
    }

    private async Task RemoveFromIndexAsync(ControlOperation operation)
    {
        string normalizedNode = operation.Node.ToLowerInvariant();
        if (redis is not null)
        {
            await redis.GetDatabase().SortedSetRemoveAsync(OperationIndexKey(normalizedNode), operation.Id);
            return;
        }

        if (_localOperationsByNode.TryGetValue(normalizedNode, out ConcurrentDictionary<string, DateTimeOffset>? index))
        {
            index.TryRemove(operation.Id, out _);
            if (index.IsEmpty)
            {
                _localOperationsByNode.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, DateTimeOffset>>(normalizedNode, index));
            }
        }
    }

    private DistributedCacheEntryOptions CacheOptions() => new() { AbsoluteExpirationRelativeToNow = _retention };
    private string OperationKey(string reservationHash) => $"{_prefix}operation:{{{reservationHash}}}";
    private string IdempotencyKey(string reservationHash) => $"{_prefix}idempotency:{{{reservationHash}}}";
    private string OperationIndexKey(string node) => $"{_prefix}operation-index:{CreateNodeHash(node)}";
    private string OwnerKey(string ownerId) => $"{_prefix}owner:{ownerId}";

    private static string CreateNodeHash(string node)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node))).ToLowerInvariant();
    }

    private static string CreateReservationHash(string node, string type, string key)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{node}\n{type}\n{key}"))).ToLowerInvariant();
    }

    private static IdempotencyCacheEntry DeserializeIndex(string json) => JsonSerializer.Deserialize<IdempotencyCacheEntry>(json, _jsonOptions)!;
}
