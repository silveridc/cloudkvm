using Control.Model.Response;

namespace Control;

/// <summary>把未处理异常与 404/405 响应统一包装成 JSON API 响应的中间件。</summary>
public sealed class ApiResponseMiddleware(RequestDelegate next, ILogger<ApiResponseMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled HTTP request error.");
            await Response.WriteAsync(context, Resources.Localization.API.internal_error, ResponseCode.InternalError, StatusCodes.Status500InternalServerError);
            return;
        }

        if (context.Response.HasStarted || context.WebSockets.IsWebSocketRequest)
        {
            return;
        }

        if (context.Response.StatusCode == StatusCodes.Status404NotFound)
        {
            await Response.WriteAsync(context, Resources.Localization.API.resource_not_found, ResponseCode.NotFound, StatusCodes.Status404NotFound);
        }
        else if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed)
        {
            await Response.WriteAsync(context, Resources.Localization.API.method_not_allowed, ResponseCode.RequestError, StatusCodes.Status405MethodNotAllowed);
        }
    }
}
