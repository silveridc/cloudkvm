using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Cluster.Services;

public sealed class ClusterExceptionInterceptor(ILogger<ClusterExceptionInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        try
        {
            return await continuation(request, context);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled."));
        }
        catch (VirshCommandException exception)
        {
            logger.LogWarning(exception, "libvirt command failed for {Method}.", context.Method);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.StandardError));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled cluster RPC error for {Method}.", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, exception.Message));
        }
    }
}
