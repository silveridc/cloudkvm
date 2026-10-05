namespace Control.Model.Response;

/// <summary>宿主机指标；数值字段在集群代理测不到时为 null，不伪造。</summary>
public sealed record ClusterHostMetricsResponse(
    double? CpuUsageRatio,
    uint? LogicalCpuCount,
    ulong? MemoryTotalBytes,
    ulong? MemoryAvailableBytes,
    string VirtualMachineDirectory,
    ulong? DiskTotalBytes,
    ulong? DiskAvailableBytes,
    ulong? UptimeSeconds);

/// <summary>单台虚拟机指标；数值字段测不到时为 null，不伪造。</summary>
public sealed record ClusterVirtualMachineMetricsResponse(
    double? CpuUsageRatio,
    ulong? MemoryUsedBytes,
    uint? AllocatedVirtualCpuCount,
    ulong? AllocatedMemoryBytes);

/// <summary>单节点虚拟机资源分配汇总。</summary>
public sealed record ClusterVirtualMachineSummaryResponse(
    int Count,
    ulong? AllocatedVirtualCpuCount,
    ulong? AllocatedMemoryMiB);

/// <summary>单节点汇总：在线状态、最近成功采样时间与宿主机/虚拟机指标。</summary>
public sealed record ClusterNodeSummaryResponse(
    string Node,
    bool Online,
    DateTimeOffset? LastSeen,
    ClusterHostMetricsResponse? Host,
    ClusterVirtualMachineSummaryResponse VirtualMachines);
