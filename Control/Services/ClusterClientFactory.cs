using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Control.Interface;
using Control.Model.Options;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Kvm.Contracts;

namespace Control.Services;

/// <summary>集群 gRPC 客户端工厂：按节点缓存连接，配置指纹变化时重建并释放旧通道。</summary>
public sealed class ClusterClientFactory(IOptionsMonitor<ClustersOptions> options) : IClusterClientFactory, IDisposable
{
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, CachedClusterClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public bool TryCreate(string node, out ClusterAgent.ClusterAgentClient? client)
    {
        if (!options.CurrentValue.Nodes.TryGetValue(node, out ClusterNodeOptions? cluster)
            || !Uri.TryCreate(cluster.Address, UriKind.Absolute, out Uri? address)
            || !string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(cluster.Rpc.Token))
        {
            client = null;
            return false;
        }

        string cacheKey = node.ToLowerInvariant();
        string fingerprint = $"{address.AbsoluteUri}\n{cluster.Rpc.Token}\n{cluster.AllowSelfSignedCertificate}";
        lock (_syncRoot)
        {
            if (_clients.TryGetValue(cacheKey, out CachedClusterClient? cached) && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                client = cached.Client;
                return true;
            }

            SocketsHttpHandler httpHandler = new();
            if (cluster.AllowSelfSignedCertificate)
            {
                httpHandler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is X509Certificate2
                    && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0;
            }
            GrpcChannel channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new RpcPathHandler(httpHandler)
            });
            ClusterAgent.ClusterAgentClient createdClient = new(channel.Intercept(new ClusterAuthenticationInterceptor(cluster.Rpc.Token)));
            CachedClusterClient replacement = new(fingerprint, channel, httpHandler, createdClient);
            if (_clients.TryGetValue(cacheKey, out CachedClusterClient? previous))
            {
                previous.Dispose();
            }
            _clients[cacheKey] = replacement;
            client = createdClient;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            foreach (CachedClusterClient client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();
        }
    }
}
