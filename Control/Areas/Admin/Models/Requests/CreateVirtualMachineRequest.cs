namespace Control.Areas.Admin.Models.Requests;

public sealed record CreateVirtualMachineRequest(
    string Name,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    string BaseImage,
    string BridgeName,
    string MacAddress,
    CloudInitRequest CloudInit,
    bool Start);
