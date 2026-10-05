using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Cluster.Services;

/// <summary>把集群 gRPC 服务端的未处理异常统一翻译为 RpcException。</summary>
public sealed class ClusterExceptionInterceptor(ILogger<ClusterExceptionInterceptor> logger) : Interceptor
{
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        try
        {
            await continuation(requestStream, responseStream, context);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request was cancelled."));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled cluster streaming RPC error for {Method}.", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "The cluster stream failed."));
        }
    }

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
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The libvirt operation could not be completed."));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled cluster RPC error for {Method}.", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "The cluster operation failed."));
        }
    }
}
