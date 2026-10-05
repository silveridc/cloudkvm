using Cluster.Services;

namespace Cluster.Interface;

/// <summary>虚拟机持久/运行配置的查询、更新与系统盘扩容。</summary>
public interface IVirtualMachineConfigurationManager
{
    Task<VirshVirtualMachineConfig> GetConfigAsync(string name, CancellationToken cancellationToken);
    Task<VirshVirtualMachineConfigUpdate> UpdateConfigAsync(string name, uint? virtualCpuCount, ulong? memoryMiB, bool applyLive, CancellationToken cancellationToken);
    Task<VirshVirtualMachineSystemDisk> ResizeSystemDiskAsync(string name, ulong sizeGiB, CancellationToken cancellationToken);
}
