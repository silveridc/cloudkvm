namespace Control.Model.Response;

/// <summary>VNC 控制台访问地址与过期时间。</summary>
public sealed record VncConsoleResponse(string Url, DateTimeOffset ExpiresAt);
