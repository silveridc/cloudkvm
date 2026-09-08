namespace Cluster.Services;

public sealed record HostNatRule(string Id, string Protocol, string ListenAddress, ushort ListenPort, string TargetAddress, ushort TargetPort);
