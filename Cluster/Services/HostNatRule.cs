namespace Cluster.Services;

/// <summary>宿主机 NAT 规则：监听地址端口转发到目标地址端口。</summary>
public sealed record HostNatRule(string Id, string Protocol, string ListenAddress, ushort ListenPort, string TargetAddress, ushort TargetPort);
