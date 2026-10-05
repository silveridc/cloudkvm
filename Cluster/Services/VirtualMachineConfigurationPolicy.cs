using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cluster.Services;

/// <summary>配置更新的校验与换算规则；硬上限与创建时的限制一致，节点上限来自 ProvisioningOptions 并同样 clamp。</summary>
public static class VirtualMachineConfigurationPolicy
{
    public const ulong MinimumMemoryMiB = 512;
    public const ulong MaximumMemoryMiB = 1_048_576;
    public const uint MinimumVirtualCpuCount = 1;
    public const uint MaximumVirtualCpuCount = 256;
    public const ulong MinimumDiskGiB = 1;
    public const ulong MaximumDiskGiB = 1_048_576;
    public const string SystemDiskFileName = "disk.qcow2";
    public const string SystemDiskTargetDevice = "vda";

    public static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsNameValid(string name)
    {
        return NamePattern.IsMatch(name);
    }

    public static void ValidateUpdate(uint? virtualCpuCount, ulong? memoryMiB, ulong configuredMaximumMemoryMiB, uint configuredMaximumVirtualCpuCount)
    {
        if (virtualCpuCount is null && memoryMiB is null)
        {
            throw new ArgumentException("A virtual CPU count or a memory amount is required.", nameof(virtualCpuCount));
        }
        if (virtualCpuCount is { } cpu && (cpu is 0 || cpu > MaximumVirtualCpuCount))
        {
            throw new ArgumentException("Virtual CPU count must be 1-256.", nameof(virtualCpuCount));
        }
        if (memoryMiB is { } memory && memory is < MinimumMemoryMiB or > MaximumMemoryMiB)
        {
            throw new ArgumentException("Memory must be 512-1048576 MiB.", nameof(memoryMiB));
        }

        ulong maximumMemoryMiB = Math.Clamp(configuredMaximumMemoryMiB, MinimumMemoryMiB, MaximumMemoryMiB);
        uint maximumVirtualCpuCount = Math.Clamp(configuredMaximumVirtualCpuCount, MinimumVirtualCpuCount, MaximumVirtualCpuCount);
        if (memoryMiB is { } requestedMemory && requestedMemory > maximumMemoryMiB)
        {
            throw new ArgumentException("Requested memory exceeds the configured node limit.", nameof(memoryMiB));
        }
        if (virtualCpuCount is { } requestedCpu && requestedCpu > maximumVirtualCpuCount)
        {
            throw new ArgumentException("Requested virtual CPU count exceeds the configured node limit.", nameof(virtualCpuCount));
        }
    }

    public static void ValidateDiskGrowth(ulong currentBytes, ulong requestedGiB)
    {
        if (requestedGiB is < MinimumDiskGiB or > MaximumDiskGiB)
        {
            throw new ArgumentException("Disk size must be 1-1048576 GiB.", nameof(requestedGiB));
        }

        ulong requestedBytes = GiBToBytes(requestedGiB);
        if (requestedBytes <= currentBytes)
        {
            throw new InvalidOperationException("The system disk can only be expanded, not shrunk.");
        }
    }

    public static ulong GiBToBytes(ulong gib)
    {
        return checked(gib * 1024 * 1024 * 1024);
    }

    /// <summary>换算为整 GiB，向上取整，保证不会报小于实际可容纳容量的值。</summary>
    public static ulong BytesToGiB(ulong bytes)
    {
        return (bytes + (1024UL * 1024 * 1024 - 1)) / (1024UL * 1024 * 1024);
    }

    /// <summary>
    /// 定位域的唯一受管系统盘（vda），并验证其 source file 恰好是 VM 目录下的 disk.qcow2；
    /// 其他布局一律拒绝，扩容请求因此永远无法触达任意宿主路径。
    /// </summary>
    public static VirshDomainDisk FindManagedSystemDisk(IReadOnlyList<VirshDomainDisk> disks, string virtualMachineDirectory)
    {
        VirshDomainDisk? systemDisk = disks.SingleOrDefault(disk =>
            string.Equals(disk.TargetDevice, SystemDiskTargetDevice, StringComparison.Ordinal)
            && string.Equals(disk.Device, "disk", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The virtual machine does not have a managed system disk.");

        if (!string.Equals(systemDisk.DriverType, "qcow2", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(systemDisk.SourceFile))
        {
            throw new InvalidOperationException("The virtual machine system disk is not a managed qcow2 image.");
        }

        string root = Path.GetFullPath(virtualMachineDirectory);
        string expected = Path.GetFullPath(Path.Combine(root, SystemDiskFileName));
        string actual = Path.GetFullPath(systemDisk.SourceFile);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The virtual machine system disk is not managed by KvmControl.");
        }

        return systemDisk;
    }

    /// <summary>解析 VM 在受管根目录下的存储目录，沿用删除受管存储时的目录越界与符号链接防御。</summary>
    public static string ResolveVirtualMachineDirectory(string root, string name)
    {
        string fullRoot = Path.GetFullPath(root);
        string directory = Path.GetFullPath(Path.Combine(fullRoot, name));
        if (!directory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Virtual machine storage is outside the managed directory.");
        }

        DirectoryInfo directoryInfo = new(directory);
        if (!directoryInfo.Exists)
        {
            throw new InvalidOperationException("Virtual machine storage does not exist.");
        }
        if (directoryInfo.LinkTarget is not null || (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Symbolic-link virtual machine storage is not allowed.");
        }

        return directory;
    }
}
