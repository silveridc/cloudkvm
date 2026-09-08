using Control.Model;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Control.Services;

public sealed class VncConsoleTicketStore(
    IDistributedCache cache,
    IOptions<Model.Options.CacheOptions> options,
    IConnectionMultiplexer? redis = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim localLock = new(1, 1);
    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(Math.Clamp(options.Value.VncTicketLifetimeMinutes, 1, 30));
    private readonly string prefix = $"{options.Value.InstanceName}:vnc:";
    private readonly ConcurrentDictionary<string, byte> localTickets = new(StringComparer.Ordinal);

    public async Task<VncTicketCreation> CreateAsync(string node, string virtualMachineName, string clusterSessionId, CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        VncConsoleTicket ticket = new(node, virtualMachineName, clusterSessionId, expiresAt);
        await cache.SetStringAsync(Key(token), JsonSerializer.Serialize(ticket, JsonOptions), new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt
        }, cancellationToken);
        localTickets[token] = 0;
        return new VncTicketCreation(token, expiresAt);
    }

    public async Task<VncConsoleTicket?> TakeAsync(string token, CancellationToken cancellationToken)
    {
        string key = Key(token);
        string? json;
        if (redis is not null)
        {
            RedisResult result = await redis.GetDatabase().ScriptEvaluateAsync(
                "local value=redis.call('GET',KEYS[1]); if value then redis.call('DEL',KEYS[1]) end; return value",
                [key]);
            json = result.IsNull ? null : (string)result!;
        }
        else
        {
            await localLock.WaitAsync(cancellationToken);
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
                localLock.Release();
            }
        }
        localTickets.TryRemove(token, out _);

        VncConsoleTicket? ticket = json is null ? null : JsonSerializer.Deserialize<VncConsoleTicket>(json, JsonOptions);
        return ticket?.ExpiresAt > DateTimeOffset.UtcNow ? ticket : null;
    }

    public async Task RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        foreach (string token in localTickets.Keys)
        {
            if (await cache.GetAsync(Key(token), cancellationToken) is null)
            {
                localTickets.TryRemove(token, out _);
            }
        }
    }

    private string Key(string token) => $"{prefix}{token}";
}
