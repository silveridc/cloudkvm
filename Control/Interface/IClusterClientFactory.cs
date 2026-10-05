namespace Control.Interface;

/// <summary>按节点名创建集群 gRPC 客户端的工厂契约。</summary>
public interface IClusterClientFactory
{
    bool TryCreate(string node, out Kvm.Contracts.ClusterAgent.ClusterAgentClient? client);
}
