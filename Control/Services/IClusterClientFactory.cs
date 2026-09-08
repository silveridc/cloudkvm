namespace Control.Services;

public interface IClusterClientFactory
{
    bool TryCreate(string node, out Kvm.Contracts.ClusterAgent.ClusterAgentClient? client);
}
