using Grpc.Core;
using Kvm.Contracts;

namespace Cluster.Services;

public interface IVncConsoleService
{
    Task<string> OpenAsync(string name, CancellationToken cancellationToken);
    Task ProxyAsync(IAsyncStreamReader<VncProxyFrame> requestStream, IServerStreamWriter<VncProxyFrame> responseStream, CancellationToken cancellationToken);
    Task CleanupExpiredSessionsAsync();
}
