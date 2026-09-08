using Control.Model;
using System.Net.WebSockets;
using Google.Protobuf;
using Grpc.Core;
using Kvm.Contracts;

namespace Control.Services;

public sealed class VncConsoleProxy(IClusterClientFactory clusterClientFactory, VncConsoleTicketStore ticketStore)
{
    public async Task ProxyAsync(HttpContext context, string ticketToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        VncConsoleTicket? ticket = await ticketStore.TakeAsync(ticketToken, context.RequestAborted);
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
        try
        {
            await clusterCall.RequestStream.WriteAsync(new VncProxyFrame { SessionId = ticket.ClusterSessionId });
            Task browserToCluster = CopyBrowserToClusterAsync(browserSocket, clusterCall.RequestStream, context.RequestAborted);
            Task clusterToBrowser = CopyClusterToBrowserAsync(browserSocket, clusterCall.ResponseStream, context.RequestAborted);
            await Task.WhenAny(browserToCluster, clusterToBrowser);
            await clusterCall.RequestStream.CompleteAsync();
            await IgnoreCancellationAsync(browserToCluster);
            await IgnoreCancellationAsync(clusterToBrowser);
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
