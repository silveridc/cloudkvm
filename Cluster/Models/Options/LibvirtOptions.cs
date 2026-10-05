namespace Cluster.Models.Options;

/// <summary>libvirt 连接配置（virsh --connect 的 URI）。</summary>
public sealed class LibvirtOptions
{
    public const string SectionName = "Libvirt";

    public string Uri { get; init; } = "qemu:///system";
}
