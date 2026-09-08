namespace Cluster.Services;

public sealed record HostBridge(string Name, HostBridgeType Type, IReadOnlyList<string> Ports, bool Up);
