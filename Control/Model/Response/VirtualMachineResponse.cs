namespace Control.Model.Response;

public sealed record VirtualMachineResponse(
    string Name,
    string Uuid,
    int Id,
    VirtualMachineState State,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    bool Persistent);
