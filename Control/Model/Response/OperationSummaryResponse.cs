namespace Control.Model.Response;

/// <summary>异步操作列表项摘要。</summary>
public sealed record OperationSummaryResponse(
    string Id,
    string Type,
    string Node,
    OperationStatus Status,
    string? GrpcStatusCode,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
