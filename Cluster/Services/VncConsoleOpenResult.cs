namespace Cluster.Services;

/// <summary>VNC 会话开启结果：会话标识与临时密码。</summary>
public sealed record VncConsoleOpenResult(string SessionId, string Password);
