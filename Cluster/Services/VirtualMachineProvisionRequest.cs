namespace Cluster.Services;

public sealed record VirtualMachineProvisionRequest(
    string Name,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    string BaseImage,
    string BridgeName,
    string MacAddress,
    CloudInitConfiguration CloudInit,
    bool Start);
