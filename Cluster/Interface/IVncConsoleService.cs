using Cluster.Services;
using Grpc.Core;
using Kvm.Contracts;

namespace Cluster.Interface;

public interface IVncConsoleService
{
    Task<VncConsoleOpenResult> OpenAsync(string name, CancellationToken cancellationToken);
    Task ProxyAsync(IAsyncStreamReader<VncProxyFrame> requestStream, IServerStreamWriter<VncProxyFrame> responseStream, CancellationToken cancellationToken);
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken);
}
