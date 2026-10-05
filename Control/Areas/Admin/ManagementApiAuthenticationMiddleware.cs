using Control;
using Control.Model.Options;
using Control.Model.Response;
using System.Security.Cryptography;
using System.Text;

namespace Control.Areas.Admin;

/// <summary>管理 API（/admin/v1）的令牌认证中间件，恒定时间比对。</summary>
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
        if (string.IsNullOrWhiteSpace(token) || !FixedTimeEquals(token, suppliedToken))
        {
            await Response.WriteAsync(context, Resources.Localization.API.admin_authentication_failed, ResponseCode.Unauthorized, StatusCodes.Status401Unauthorized);
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }
        byte[] expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        byte[] actualBytes = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
