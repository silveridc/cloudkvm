namespace Control.Model.Response;

/// <summary>模型验证失败响应：字段到错误列表的映射。</summary>
public sealed record ValidationErrorResponse(IReadOnlyDictionary<string, string[]> Errors);
