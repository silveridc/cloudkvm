using System.Text.Json;
using System.Text.Json.Serialization;
using Control.Model;
using Control.Model.Response;

namespace Control.Services;

/// <summary>操作的可序列化缓存快照，Result 以 JSON 元素保存。</summary>
public sealed record OperationCacheEntry(
    string Id,
    string IdempotencyKey,
    string Fingerprint,
    string Type,
    string Node,
    string OwnerId,
    OperationStatus Status,
    JsonElement? Result,
    string? GrpcStatusCode,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static OperationCacheEntry From(ControlOperation operation)
    {
        return new OperationCacheEntry(
            operation.Id,
            operation.IdempotencyKey,
            operation.Fingerprint,
            operation.Type,
            operation.Node,
            operation.OwnerId,
            operation.Status,
            operation.Result is null ? null : JsonSerializer.SerializeToElement(operation.Result, _jsonOptions),
            operation.GrpcStatusCode,
            operation.Error,
            operation.CreatedAt,
            operation.CompletedAt);
    }

    public ControlOperation ToOperation()
    {
        return new ControlOperation(Id, IdempotencyKey, Fingerprint, Type, Node, OwnerId, CreatedAt)
        {
            Status = Status,
            Result = Result,
            GrpcStatusCode = GrpcStatusCode,
            Error = Error,
            CompletedAt = CompletedAt
        };
    }
}
