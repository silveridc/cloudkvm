namespace Control.Model.Response;

public enum ResponseCode
{
    Success = 200100,
    NotFound = 311000,
    InternalError = 311100,
    RequestError = 411000,
    Unauthorized = 411001,
    Conflict = 411002
}
