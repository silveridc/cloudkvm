using Control.Model;
using System.Net.WebSockets;
using Google.Protobuf;
using Grpc.Core;
using Kvm.Contracts;
using Control.Interface;

namespace Control.Services;

public sealed class VncConsoleProxy(IClusterClientFactory clusterClientFactory, VncConsoleTicketStore ticketStore)
{
    private const int MaximumFrameBytes = 1024 * 1024;
    public async Task ProxyAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest
            || !IsSameOrigin(context)
            || !context.Request.Cookies.TryGetValue("kvm-console-ticket", out string? ticketToken))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        VncConsoleTicket? ticket = await ticketStore.TakeAsync(ticketToken, context.RequestAborted);
        context.Response.Cookies.Delete("kvm-console-ticket", new CookieOptions { Path = "/api/v1/consoles" });
        if (ticket is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using WebSocket browserSocket = await context.WebSockets.AcceptWebSocketAsync();
        if (!clusterClientFactory.TryCreate(ticket.Node, out ClusterAgent.ClusterAgentClient? client) || client is null)
        {
            await browserSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, Resources.Localization.API.resource_not_found, CancellationToken.None);
            return;
        }
        AsyncDuplexStreamingCall<VncProxyFrame, VncProxyFrame> clusterCall = client.ProxyVnc(cancellationToken: context.RequestAborted);
        using CancellationTokenSource proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        try
        {
            await clusterCall.RequestStream.WriteAsync(new VncProxyFrame { SessionId = ticket.ClusterSessionId }, proxyCancellation.Token);
            Task browserToCluster = CopyBrowserToClusterAsync(browserSocket, clusterCall.RequestStream, proxyCancellation.Token);
            Task clusterToBrowser = CopyClusterToBrowserAsync(browserSocket, clusterCall.ResponseStream, proxyCancellation.Token);
            // 任一方向结束后即取消另一方向，避免对端阻塞、以及 CompleteAsync 与在飞 WriteAsync 竞态。
            await Task.WhenAny(browserToCluster, clusterToBrowser);
            proxyCancellation.Cancel();
            await IgnoreCancellationAsync(browserToCluster);
            await IgnoreCancellationAsync(clusterToBrowser);
            try
            {
                await clusterCall.RequestStream.CompleteAsync();
            }
            catch (RpcException)
            {
                // 调用已被对端终止，无需再半关闭。
            }
        }
        finally
        {
            clusterCall.Dispose();
            if (browserSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await browserSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
    }

    private static bool IsSameOrigin(HttpContext context)
    {
        string? origin = context.Request.Headers.Origin;
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri)
            && string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyBrowserToClusterAsync(WebSocket browserSocket, IClientStreamWriter<VncProxyFrame> requestStream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[32 * 1024];
        using MemoryStream message = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await browserSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new WebSocketException("VNC proxy only accepts binary WebSocket frames.");
            }

            if (message.Length + result.Count > MaximumFrameBytes)
            {
                await browserSocket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "VNC frame exceeded the maximum size.", CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            await requestStream.WriteAsync(new VncProxyFrame { Data = ByteString.CopyFrom(message.GetBuffer(), 0, checked((int)message.Length)) });
            message.SetLength(0);
        }
    }

    private static async Task CopyClusterToBrowserAsync(WebSocket browserSocket, IAsyncStreamReader<VncProxyFrame> responseStream, CancellationToken cancellationToken)
    {
        while (await responseStream.MoveNext(cancellationToken))
        {
            VncProxyFrame frame = responseStream.Current;
            if (frame.Data.IsEmpty)
            {
                continue;
            }
            if (frame.Data.Length > MaximumFrameBytes)
            {
                await browserSocket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "VNC frame exceeded the maximum size.", CancellationToken.None);
                return;
            }
            await browserSocket.SendAsync(frame.Data.Memory, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
