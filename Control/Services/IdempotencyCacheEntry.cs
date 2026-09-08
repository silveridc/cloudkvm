namespace Control.Services;

public sealed record IdempotencyCacheEntry(string OperationId, string Fingerprint, OperationCacheEntry Operation);
