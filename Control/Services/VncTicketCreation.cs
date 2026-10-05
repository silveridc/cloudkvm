namespace Control.Services;

/// <summary>VNC 票据创建结果：令牌、控制台密码与过期时间。</summary>
public sealed record VncTicketCreation(string Token, string Password, DateTimeOffset ExpiresAt);
