using Kvm.Contracts;

namespace Cluster.Interface;

/// <summary>采集一帧节点与虚拟机指标快照。</summary>
public interface INodeMetricsCollector
{
    Task<NodeMetricsReply> GetNodeMetricsAsync(CancellationToken cancellationToken);
}
