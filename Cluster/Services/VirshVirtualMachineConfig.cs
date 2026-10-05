namespace Cluster.Services;

/// <summary>VM 的受管系统盘（仅暴露相对文件名，不含宿主路径）。</summary>
public sealed record VirshVirtualMachineSystemDisk(string File, ulong SizeGiB);

/// <summary>VM 的持久与运行配置快照。</summary>
public sealed record VirshVirtualMachineConfig(
    string Name,
    VirshVirtualMachineState State,
    bool Running,
    uint PersistentVirtualCpuCount,
    ulong PersistentMemoryMiB,
    uint LiveVirtualCpuCount,
    ulong LiveMemoryMiB,
    VirshVirtualMachineSystemDisk SystemDisk);

/// <summary>配置更新的结果；persistent/live 两个维度各自表达是否生效。</summary>
public sealed record VirshVirtualMachineConfigUpdate(
    bool PersistentApplied,
    bool LiveApplied,
    string LiveMessage,
    uint PersistentVirtualCpuCount,
    ulong PersistentMemoryMiB,
    uint LiveVirtualCpuCount,
    ulong LiveMemoryMiB);
