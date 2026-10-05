using Cluster.Services;

namespace Cluster.Interface;

public interface IVirshClient
{
    Task<VirshHostStatus> GetHostStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VirshVirtualMachine>> ListVirtualMachinesAsync(CancellationToken cancellationToken);
    Task<string> GetDomainStatsAsync(CancellationToken cancellationToken);
    Task<VirshVirtualMachine?> GetVirtualMachineAsync(string name, CancellationToken cancellationToken);
    Task<bool> DomainExistsAsync(string name, CancellationToken cancellationToken);
    Task<VirshVirtualMachine> ChangePowerStateAsync(string name, VirshPowerAction action, CancellationToken cancellationToken);
    Task DefineAsync(string domainXmlPath, CancellationToken cancellationToken);
    Task UndefineAsync(string name, CancellationToken cancellationToken);
    Task<bool> UndefineIfUuidMatchesAsync(string name, string uuid, CancellationToken cancellationToken);
    Task<int> GetVncPortAsync(string name, CancellationToken cancellationToken);
    Task<bool> IsManagedAsync(string name, CancellationToken cancellationToken);
    Task<VirshDomainXml> DumpXmlAsync(string name, bool inactive, CancellationToken cancellationToken);
    Task SetVirtualCpuCountLiveAsync(string name, uint virtualCpuCount, CancellationToken cancellationToken);
    Task SetMemoryLiveAsync(string name, ulong memoryKiB, CancellationToken cancellationToken);
    Task ResizeBlockDeviceAsync(string name, string targetDevice, ulong sizeBytes, CancellationToken cancellationToken);
    Task ResizeDiskImageAsync(string diskPath, ulong sizeBytes, CancellationToken cancellationToken);
    Task<ulong> GetDiskImageVirtualSizeBytesAsync(string diskPath, CancellationToken cancellationToken);
    Task SetVncPasswordAsync(string name, string password, TimeSpan lifetime, CancellationToken cancellationToken);
    Task ExpireVncPasswordAsync(string name, CancellationToken cancellationToken);
}
