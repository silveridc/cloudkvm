using Grpc.Core;
using Grpc.Core.Interceptors;
using System.Security.Cryptography;
using System.Text;

namespace Cluster.Services;

/// <summary>集群 gRPC 服务端的令牌认证拦截器，按请求头做恒定时间比对。</summary>
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
        if (!FixedTimeEquals(token, suppliedToken))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Cluster RPC authentication failed."));
        }
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }
        byte[] expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        byte[] actualBytes = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
