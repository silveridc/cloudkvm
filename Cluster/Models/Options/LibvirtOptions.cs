namespace Cluster.Models.Options;

public sealed class LibvirtOptions
{
    public const string SectionName = "Libvirt";

    public string Uri { get; init; } = "qemu:///system";
}
