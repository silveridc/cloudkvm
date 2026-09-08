using Control;
using Control.Model.Options;
using Control.Model.Response;

namespace Control.Areas.Admin;

public sealed class ManagementApiAuthenticationMiddleware(RequestDelegate next, IOptions<ManagementApiOptions> options)
{
    private const string TokenHeader = "x-kvmcontrol-token";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin/v1"))
        {
            await next(context);
            return;
        }

        string token = options.Value.Token;
        string? suppliedToken = context.Request.Headers[TokenHeader];
        if (string.IsNullOrWhiteSpace(token) || !string.Equals(token, suppliedToken, StringComparison.Ordinal))
        {
            await Response.WriteAsync(context, Resources.Localization.API.admin_authentication_failed, ResponseCode.Unauthorized, StatusCodes.Status401Unauthorized);
            return;
        }

        await next(context);
    }
}
