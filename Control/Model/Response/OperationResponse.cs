namespace Control.Model.Response;

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
