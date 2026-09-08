using Control.Model.Options;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Kvm.Contracts;

namespace Control.Services;

public sealed class ClusterClientFactory(IOptionsMonitor<ClustersOptions> options) : IClusterClientFactory
{
    public bool TryCreate(string node, out ClusterAgent.ClusterAgentClient? client)
    {
        if (!options.CurrentValue.Nodes.TryGetValue(node, out ClusterNodeOptions? cluster) || string.IsNullOrWhiteSpace(cluster.Address))
        {
            client = null;
            return false;
        }

        SocketsHttpHandler httpHandler = new();
        GrpcChannel channel = GrpcChannel.ForAddress(cluster.Address, new GrpcChannelOptions
        {
            HttpHandler = new RpcPathHandler(httpHandler)
        });
        client = new ClusterAgent.ClusterAgentClient(channel.Intercept(new ClusterAuthenticationInterceptor(cluster.Rpc.Token)));
        return true;
    }
}
