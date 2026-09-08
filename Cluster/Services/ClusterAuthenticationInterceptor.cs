using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Cluster.Services;

public sealed class ClusterAuthenticationInterceptor(IOptions<Models.Options.RpcOptions> options) : Interceptor
{
    private const string TokenHeader = "x-kvmcontrol-token";

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        Validate(context);
        return continuation(request, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        Validate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Validate(ServerCallContext context)
    {
        string token = options.Value.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Cluster RPC token is not configured."));
        }

        string? suppliedToken = context.RequestHeaders.GetValue(TokenHeader);
        if (!string.Equals(token, suppliedToken, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Cluster RPC authentication failed."));
        }
    }
}
