namespace Cluster.Services;

/// <summary>一个已开启的 VNC 会话，持有需随会话释放的 websockify 进程与令牌文件。</summary>
public sealed class VncConsoleSession(
    string id,
    string virtualMachineName,
    DateTimeOffset expiresAt,
    int websockifyPort,
    string tokenFilePath,
    System.Diagnostics.Process websockifyProcess)
{
    public string Id { get; } = id;
    public string VirtualMachineName { get; } = virtualMachineName;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public int WebsockifyPort { get; } = websockifyPort;
    public string TokenFilePath { get; } = tokenFilePath;
    public System.Diagnostics.Process WebsockifyProcess { get; } = websockifyProcess;
}
