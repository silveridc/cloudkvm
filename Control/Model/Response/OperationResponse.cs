namespace Control.Model.Response;

/// <summary>异步操作的完整状态，含结果、错误与时间戳。</summary>
public sealed record OperationResponse(
    string Id,
    string Type,
    string Node,
    OperationStatus Status,
    object? Result,
    string? GrpcStatusCode,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
