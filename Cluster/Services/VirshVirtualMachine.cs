namespace Cluster.Services;

public sealed record VirshVirtualMachine(
    string Name,
    string Uuid,
    int Id,
    VirshVirtualMachineState State,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    bool Persistent);
