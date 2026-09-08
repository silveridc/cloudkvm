using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Control.Services;

public sealed class ClusterAuthenticationInterceptor(string token) : Interceptor
{
    private const string TokenHeader = "x-kvmcontrol-token";

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(CreateContext(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> CreateContext<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        Metadata headers = [];
        if (context.Options.Headers is not null)
        {
            foreach (Metadata.Entry header in context.Options.Headers)
            {
                headers.Add(header);
            }
        }
        headers.Add(TokenHeader, token);
        CallOptions options = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, CreateContext(context));
    }
}
