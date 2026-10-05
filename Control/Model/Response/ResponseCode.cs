namespace Control.Model.Response;

/// <summary>API 响应码：2 开头成功，3 开头内部错误，4 开头请求错误。</summary>
public enum ResponseCode
{
    Success = 200100,
    NotFound = 311000,
    InternalError = 311100,
    RequestError = 411000,
    Unauthorized = 411001,
    Conflict = 411002,
    ServiceUnavailable = 311101
}
