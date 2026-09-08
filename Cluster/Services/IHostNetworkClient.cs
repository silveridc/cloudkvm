namespace Cluster.Services;

public interface IHostNetworkClient
{
    Task<IReadOnlyList<HostBridge>> ListBridgesAsync(CancellationToken cancellationToken);
    Task<HostBridge> CreateBridgeAsync(string name, HostBridgeType type, IReadOnlyList<string> ports, CancellationToken cancellationToken);
    Task DeleteBridgeAsync(string name, HostBridgeType type, CancellationToken cancellationToken);
    Task<IReadOnlyList<HostNatRule>> ListNatRulesAsync(CancellationToken cancellationToken);
    Task<HostNatRule> CreateNatRuleAsync(string protocol, string listenAddress, ushort listenPort, string targetAddress, ushort targetPort, CancellationToken cancellationToken);
    Task DeleteNatRuleAsync(string id, CancellationToken cancellationToken);
}
