namespace Cluster.Models.Options;

/// <summary>VNC 控制台配置：websockify 路径、会话时长与并发上限。</summary>
public sealed class VncOptions
{
    public const string SectionName = "Vnc";

    public string WebsockifyPath { get; init; } = "/usr/bin/websockify";
    public int SessionLifetimeSeconds { get; init; } = 300;
    public int MaximumSessions { get; init; } = 32;
}
