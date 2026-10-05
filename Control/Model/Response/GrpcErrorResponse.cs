namespace Control.Model.Response;

/// <summary>gRPC 调用失败信息。</summary>
public sealed record GrpcErrorResponse(string StatusCode, string Detail);
