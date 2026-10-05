namespace Control.Model;

/// <summary>一次受控异步操作的内存态：标识、幂等键、状态与完成信号。</summary>
public sealed class ControlOperation(string id, string idempotencyKey, string fingerprint, string type, string node, string ownerId, DateTimeOffset? createdAt = null)
{
    public string Id { get; } = id;
    public string IdempotencyKey { get; } = idempotencyKey;
    public string Fingerprint { get; } = fingerprint;
    public string Type { get; } = type;
    public string Node { get; } = node;
    public string OwnerId { get; } = ownerId;
    public Model.Response.OperationStatus Status { get; set; } = Model.Response.OperationStatus.Queued;
    public object? Result { get; set; }
    public string? GrpcStatusCode { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; } = createdAt ?? DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
