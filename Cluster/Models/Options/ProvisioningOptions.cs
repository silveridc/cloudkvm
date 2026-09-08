namespace Cluster.Models.Options;

public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    public string BaseImageDirectory { get; init; } = "/var/lib/kvmcontrol/images";
    public string VirtualMachineDirectory { get; init; } = "/var/lib/kvmcontrol/virtual-machines";
    public string OvmfCodePath { get; init; } = "/usr/share/OVMF/OVMF_CODE.fd";
    public string OvmfVarsTemplatePath { get; init; } = "/usr/share/OVMF/OVMF_VARS.fd";
}
