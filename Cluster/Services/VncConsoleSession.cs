namespace Cluster.Services;

public sealed class VncConsoleSession(string id, DateTimeOffset expiresAt, int websockifyPort, string password, System.Diagnostics.Process websockifyProcess)
{
    public string Id { get; } = id;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public int WebsockifyPort { get; } = websockifyPort;
    public string Password { get; } = password;
    public System.Diagnostics.Process WebsockifyProcess { get; } = websockifyProcess;
}
