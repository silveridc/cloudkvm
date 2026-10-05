namespace Cluster.Models.Options;

/// <summary>宿主机网络操作白名单与限额：网桥前缀、NAT 网段、端口与桥接端口上限。</summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    public string[] AllowedBridgePrefixes { get; init; } = ["kvmbr"];
    public string[] AllowedPorts { get; init; } = [];
    public string[] AllowedNatListenCidrs { get; init; } = [];
    public string[] AllowedNatTargetCidrs { get; init; } = [];
    public int MaximumBridgePorts { get; init; } = 32;
}
