namespace Cluster.Services;

/// <summary>宿主机网桥信息：名称、类型、挂接的物理端口与状态。</summary>
public sealed record HostBridge(string Name, HostBridgeType Type, IReadOnlyList<string> Ports, bool Up);
