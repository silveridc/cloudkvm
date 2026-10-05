using Grpc.Net.Client;
using Kvm.Contracts;

namespace Control.Services;

/// <summary>按配置指纹缓存的集群 gRPC 客户端及其底层通道，指纹变化时整体替换。</summary>
public sealed class CachedClusterClient(
    string fingerprint,
    GrpcChannel channel,
    SocketsHttpHandler httpHandler,
    ClusterAgent.ClusterAgentClient client) : IDisposable
{
    public string Fingerprint { get; } = fingerprint;
    public ClusterAgent.ClusterAgentClient Client { get; } = client;

    public void Dispose()
    {
        channel.Dispose();
        httpHandler.Dispose();
    }
}
