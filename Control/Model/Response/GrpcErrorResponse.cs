namespace Control.Model.Response;

public sealed record GrpcErrorResponse(string StatusCode, string Detail);
