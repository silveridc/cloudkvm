namespace Cluster.Models.Options;

/// <summary>虚拟机创建配置：镜像与存储目录、OVMF 固件路径、资源与数量上限。</summary>
public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    public string BaseImageDirectory { get; init; } = "/var/lib/kvmcontrol/images";
    public string VirtualMachineDirectory { get; init; } = "/var/lib/kvmcontrol/virtual-machines";
    public string OvmfCodePath { get; init; } = "/usr/share/OVMF/OVMF_CODE.fd";
    public string OvmfVarsTemplatePath { get; init; } = "/usr/share/OVMF/OVMF_VARS.fd";
    public ulong MaximumMemoryMiB { get; init; } = 262144;
    public uint MaximumVirtualCpuCount { get; init; } = 64;
    public int MaximumVirtualMachines { get; init; } = 256;
    public string[] AllowedBridges { get; init; } = [];
}
