namespace Control.Model.Response;

/// <summary>虚拟机信息。</summary>
public sealed record VirtualMachineResponse(
    string Name,
    string Uuid,
    int Id,
    VirtualMachineState State,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    bool Persistent)
{
    // 来自后台指标存储的快照（可用时才有值），不按需现场采集。
    public ClusterVirtualMachineMetricsResponse? Metrics { get; init; }
}
