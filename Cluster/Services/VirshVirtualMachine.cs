namespace Cluster.Services;

/// <summary>virsh dominfo 解析出的虚拟机概要。</summary>
public sealed record VirshVirtualMachine(
    string Name,
    string Uuid,
    int Id,
    VirshVirtualMachineState State,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    bool Persistent,
    ulong UsedMemoryMiB = 0);
