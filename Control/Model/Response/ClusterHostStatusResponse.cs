namespace Control.Model.Response;

/// <summary>单个集群节点的宿主机状态。</summary>
public sealed record ClusterHostStatusResponse(
    string Node,
    string HostName,
    string LibvirtUri,
    string HypervisorType,
    string HypervisorVersion)
{
    // 来自后台指标存储的快照，不按需现场采集。
    public ClusterHostMetricsResponse? Metrics { get; init; }
}
