using System.Net.Http;

namespace Control.Services;

public sealed class RpcPathHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true, AbsolutePath: var path } uri && !path.StartsWith("/rpc/", StringComparison.Ordinal))
        {
            UriBuilder builder = new(uri)
            {
                Path = $"/rpc{path}"
            };
            request.RequestUri = builder.Uri;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
