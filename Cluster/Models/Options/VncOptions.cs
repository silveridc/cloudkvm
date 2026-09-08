namespace Cluster.Models.Options;

public sealed class VncOptions
{
    public const string SectionName = "Vnc";

    public string WebsockifyPath { get; init; } = "/usr/bin/websockify";
    public int SessionLifetimeSeconds { get; init; } = 300;
}
