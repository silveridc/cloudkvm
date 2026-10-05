using Control.Model;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Control.Services;

/// <summary>VNC 一次性票据存取：创建、取用即删与过期清理，支持 Redis 或本地缓存。</summary>
public sealed class VncConsoleTicketStore(
    IDistributedCache cache,
    IOptions<Model.Options.CacheOptions> options,
    IConnectionMultiplexer? redis = null)
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _localLock = new(1, 1);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(Math.Clamp(options.Value.VncTicketLifetimeMinutes, 1, 30));
    private readonly string _prefix = $"{options.Value.InstanceName}:vnc:";
    private readonly ConcurrentDictionary<string, byte> _localTickets = new(StringComparer.Ordinal);

    public async Task<VncTicketCreation> CreateAsync(string node, string virtualMachineName, string clusterSessionId, string password, CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
        VncConsoleTicket ticket = new(node, virtualMachineName, clusterSessionId, expiresAt);
        string json = JsonSerializer.Serialize(ticket, _jsonOptions);
        if (redis is not null)
        {
            await redis.GetDatabase().StringSetAsync(Key(token), json, _lifetime);
        }
        else
        {
            await cache.SetStringAsync(Key(token), json, new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = expiresAt
            }, cancellationToken);
            _localTickets[token] = 0;
        }
        return new VncTicketCreation(token, password, expiresAt);
    }

    public async Task<VncConsoleTicket?> TakeAsync(string token, CancellationToken cancellationToken)
    {
        string key = Key(token);
        string? json;
        if (redis is not null)
        {
            RedisValue value = await redis.GetDatabase().StringGetDeleteAsync(key);
            json = value.IsNull ? null : value.ToString();
        }
        else
        {
            await _localLock.WaitAsync(cancellationToken);
            try
            {
                json = await cache.GetStringAsync(key, cancellationToken);
                if (json is not null)
                {
                    await cache.RemoveAsync(key, cancellationToken);
                }
            }
            finally
            {
                _localLock.Release();
            }
        }
        _localTickets.TryRemove(token, out _);

        VncConsoleTicket? ticket = json is null ? null : JsonSerializer.Deserialize<VncConsoleTicket>(json, _jsonOptions);
        return ticket?.ExpiresAt > DateTimeOffset.UtcNow ? ticket : null;
    }

    public async Task RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        foreach (string token in _localTickets.Keys)
        {
            if (await cache.GetAsync(Key(token), cancellationToken) is null)
            {
                _localTickets.TryRemove(token, out _);
            }
        }
    }

    private string Key(string token)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        return $"{_prefix}{hash}";
    }
}
