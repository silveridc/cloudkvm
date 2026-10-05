namespace Control.Services;

/// <summary>幂等键缓存条目：操作标识、请求指纹与操作快照。</summary>
public sealed record IdempotencyCacheEntry(string OperationId, string Fingerprint, OperationCacheEntry Operation);
