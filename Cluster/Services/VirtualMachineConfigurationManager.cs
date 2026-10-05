using System.Text;
using Microsoft.Extensions.Options;
using Cluster.Interface;

namespace Cluster.Services;

/// <summary>
/// 虚拟机配置的查询、更新与系统盘扩容，全部在 per-VM 锁内执行（与创建/删除串行），
/// 且只作用于经 dumpxml 验证属于该 VM 受管目录的唯一 vda 磁盘。
/// </summary>
public sealed class VirtualMachineConfigurationManager(
    IOptions<Models.Options.ProvisioningOptions> options,
    IVirshClient virshClient,
    IVirtualMachineLockManager lockManager,
    ILogger<VirtualMachineConfigurationManager> logger) : IVirtualMachineConfigurationManager
{
    public async Task<VirshVirtualMachineConfig> GetConfigAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        return await lockManager.RunAsync(name, async _ =>
        {
            Models.Options.ProvisioningOptions configuration = options.Value;
            VirshVirtualMachine virtualMachine = await RequireManagedVirtualMachineAsync(name, cancellationToken);
            VirshDomainXml definition = await virshClient.DumpXmlAsync(name, inactive: true, cancellationToken);
            VirshVirtualMachineSystemDisk systemDisk = await GetSystemDiskAsync(configuration, name, definition.Definition, cancellationToken);

            bool running = virtualMachine.State == VirshVirtualMachineState.Running;
            return new VirshVirtualMachineConfig(
                virtualMachine.Name,
                virtualMachine.State,
                running,
                definition.Definition.VirtualCpuCount,
                definition.Definition.MaximumMemoryKiB / 1024,
                running ? virtualMachine.VirtualCpuCount : definition.Definition.VirtualCpuCount,
                running ? virtualMachine.UsedMemoryMiB : definition.Definition.CurrentMemoryKiB / 1024,
                systemDisk);
        }, cancellationToken);
    }

    public async Task<VirshVirtualMachineConfigUpdate> UpdateConfigAsync(string name, uint? virtualCpuCount, ulong? memoryMiB, bool applyLive, CancellationToken cancellationToken)
    {
        ValidateName(name);
        Models.Options.ProvisioningOptions configuration = options.Value;
        VirtualMachineConfigurationPolicy.ValidateUpdate(virtualCpuCount, memoryMiB, configuration.MaximumMemoryMiB, configuration.MaximumVirtualCpuCount);

        return await lockManager.RunAsync(name, async _ =>
        {
            VirshVirtualMachine virtualMachine = await RequireManagedVirtualMachineAsync(name, cancellationToken);
            VirshDomainXml definition = await virshClient.DumpXmlAsync(name, inactive: true, cancellationToken);

            uint newVirtualCpuCount = virtualCpuCount ?? definition.Definition.VirtualCpuCount;
            ulong newMemoryMiB = memoryMiB ?? definition.Definition.MaximumMemoryKiB / 1024;
            ulong newMemoryKiB = checked(newMemoryMiB * 1024);

            // 持久配置用"dumpxml --inactive → 只改 memory/currentMemory/vcpu → virsh define"生效：
            // 没有 undefine 窗口（NVRAM 与受管标记保留、UUID 不变），也避开 q35+UEFI 下
            // setmaxmem/setvcpus --config 的怪癖。
            string updatedXml = VirshDomainXmlMutator.WithCpuAndMemory(definition.Xml, newVirtualCpuCount, newMemoryKiB);
            string virtualMachineDirectory = VirtualMachineConfigurationPolicy.ResolveVirtualMachineDirectory(configuration.VirtualMachineDirectory, name);
            string stagingPath = Path.Combine(virtualMachineDirectory, ".config-update.xml");
            await File.WriteAllTextAsync(stagingPath, updatedXml, new UTF8Encoding(false), cancellationToken);
            try
            {
                SetFilePermissions(stagingPath);
                await virshClient.DefineAsync(stagingPath, cancellationToken);
            }
            finally
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Unable to remove the staging file {StagingPath}.", stagingPath);
                }
            }

            VirshDomainXml applied = await virshClient.DumpXmlAsync(name, inactive: true, cancellationToken);
            if (applied.Definition.VirtualCpuCount != newVirtualCpuCount || applied.Definition.MaximumMemoryKiB != newMemoryKiB)
            {
                throw new InvalidOperationException("The persistent virtual machine configuration could not be verified.");
            }

            (bool liveApplied, string liveMessage) = await ApplyLiveAsync(name, virtualMachine, virtualCpuCount, memoryMiB, applyLive, cancellationToken);
            VirshVirtualMachine refreshed = await virshClient.GetVirtualMachineAsync(name, cancellationToken) ?? virtualMachine;
            bool running = refreshed.State == VirshVirtualMachineState.Running;

            return new VirshVirtualMachineConfigUpdate(
                PersistentApplied: true,
                LiveApplied: liveApplied,
                LiveMessage: liveMessage,
                PersistentVirtualCpuCount: applied.Definition.VirtualCpuCount,
                PersistentMemoryMiB: applied.Definition.MaximumMemoryKiB / 1024,
                LiveVirtualCpuCount: running ? refreshed.VirtualCpuCount : applied.Definition.VirtualCpuCount,
                LiveMemoryMiB: running ? refreshed.UsedMemoryMiB : applied.Definition.CurrentMemoryKiB / 1024);
        }, cancellationToken);
    }

    public async Task<VirshVirtualMachineSystemDisk> ResizeSystemDiskAsync(string name, ulong sizeGiB, CancellationToken cancellationToken)
    {
        ValidateName(name);
        return await lockManager.RunAsync(name, async _ =>
        {
            Models.Options.ProvisioningOptions configuration = options.Value;
            VirshVirtualMachine virtualMachine = await RequireManagedVirtualMachineAsync(name, cancellationToken);
            VirshDomainXml definition = await virshClient.DumpXmlAsync(name, inactive: true, cancellationToken);
            VirshDomainDisk systemDisk = VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
                definition.Definition.Disks,
                Path.Combine(Path.GetFullPath(configuration.VirtualMachineDirectory), name));
            string diskPath = systemDisk.SourceFile!;

            ulong currentBytes = await virshClient.GetDiskImageVirtualSizeBytesAsync(diskPath, cancellationToken);
            VirtualMachineConfigurationPolicy.ValidateDiskGrowth(currentBytes, sizeGiB);
            ulong requestedBytes = VirtualMachineConfigurationPolicy.GiBToBytes(sizeGiB);

            if (virtualMachine.State == VirshVirtualMachineState.Running)
            {
                await virshClient.ResizeBlockDeviceAsync(name, systemDisk.TargetDevice, requestedBytes, cancellationToken);
            }
            else
            {
                await virshClient.ResizeDiskImageAsync(diskPath, requestedBytes, cancellationToken);
            }

            ulong actualBytes = await virshClient.GetDiskImageVirtualSizeBytesAsync(diskPath, cancellationToken);
            if (actualBytes < requestedBytes)
            {
                throw new InvalidOperationException("The system disk size could not be verified after the resize.");
            }

            return new VirshVirtualMachineSystemDisk(
                VirtualMachineConfigurationPolicy.SystemDiskFileName,
                VirtualMachineConfigurationPolicy.BytesToGiB(actualBytes));
        }, cancellationToken);
    }

    private async Task<(bool LiveApplied, string LiveMessage)> ApplyLiveAsync(
        string name,
        VirshVirtualMachine virtualMachine,
        uint? virtualCpuCount,
        ulong? memoryMiB,
        bool applyLive,
        CancellationToken cancellationToken)
    {
        if (virtualMachine.State != VirshVirtualMachineState.Running)
        {
            // 关机状态下持久配置即生效，下次启动自然一致。
            return (true, string.Empty);
        }
        if (!applyLive)
        {
            return (false, "Live application was not requested; the running virtual machine keeps its previous resources until it is restarted.");
        }

        List<string> failures = new();
        if (virtualCpuCount is { } requestedCpu && requestedCpu != virtualMachine.VirtualCpuCount)
        {
            try
            {
                await virshClient.SetVirtualCpuCountLiveAsync(name, requestedCpu, cancellationToken);
            }
            catch (VirshCommandException exception)
            {
                logger.LogWarning(exception, "Unable to apply the virtual CPU count live to {VirtualMachineName}.", name);
                failures.Add("the virtual CPU count could not be changed while running");
            }
        }
        if (memoryMiB is { } requestedMemory && checked(requestedMemory * 1024) != virtualMachine.UsedMemoryMiB * 1024)
        {
            try
            {
                await virshClient.SetMemoryLiveAsync(name, checked(requestedMemory * 1024), cancellationToken);
            }
            catch (VirshCommandException exception)
            {
                logger.LogWarning(exception, "Unable to apply the memory amount live to {VirtualMachineName}.", name);
                failures.Add("the memory amount could not be changed while running");
            }
        }

        return failures.Count == 0
            ? (true, string.Empty)
            : (false, $"The persistent configuration was updated, but {string.Join(" and ", failures)}. A restart applies the new values.");
    }

    private async Task<VirshVirtualMachineSystemDisk> GetSystemDiskAsync(
        Models.Options.ProvisioningOptions configuration,
        string name,
        VirshDomainDefinition definition,
        CancellationToken cancellationToken)
    {
        VirshDomainDisk systemDisk = VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
            definition.Disks,
            Path.Combine(Path.GetFullPath(configuration.VirtualMachineDirectory), name));
        ulong sizeBytes = await virshClient.GetDiskImageVirtualSizeBytesAsync(systemDisk.SourceFile!, cancellationToken);
        return new VirshVirtualMachineSystemDisk(
            VirtualMachineConfigurationPolicy.SystemDiskFileName,
            VirtualMachineConfigurationPolicy.BytesToGiB(sizeBytes));
    }

    private static void ValidateName(string name)
    {
        if (!VirtualMachineConfigurationPolicy.IsNameValid(name))
        {
            throw new ArgumentException("Virtual machine name is invalid.", nameof(name));
        }
    }

    private async Task<VirshVirtualMachine> RequireManagedVirtualMachineAsync(string name, CancellationToken cancellationToken)
    {
        if (!await virshClient.DomainExistsAsync(name, cancellationToken))
        {
            throw new VirtualMachineNotFoundException(name);
        }
        return await virshClient.GetVirtualMachineAsync(name, cancellationToken)
            ?? throw new InvalidOperationException("Virtual machine is not managed by KvmControl.");
    }

    private static void SetFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
