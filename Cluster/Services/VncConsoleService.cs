using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Google.Protobuf;
using Grpc.Core;
using Kvm.Contracts;

namespace Cluster.Services;

public sealed class VncConsoleService(IOptions<Models.Options.VncOptions> options, IVirshClient virshClient) : IVncConsoleService
{
    private readonly ConcurrentDictionary<string, VncConsoleSession> sessions = new(StringComparer.Ordinal);

    public async Task<string> OpenAsync(string name, CancellationToken cancellationToken)
    {
        VirshVirtualMachine? virtualMachine = await virshClient.GetVirtualMachineAsync(name, cancellationToken);
        if (virtualMachine is null)
        {
            throw new InvalidOperationException("Virtual machine was not found.");
        }
        if (virtualMachine.State != VirshVirtualMachineState.Running)
        {
            throw new InvalidOperationException("Virtual machine must be running before opening a VNC console.");
        }

        Models.Options.VncOptions configuration = options.Value;
        if (!Path.IsPathFullyQualified(configuration.WebsockifyPath) || !File.Exists(configuration.WebsockifyPath))
        {
            throw new InvalidOperationException("Configured websockify binary is not available.");
        }

        int vncPort = await virshClient.GetVncPortAsync(name, cancellationToken);
        int websockifyPort = ReserveLoopbackPort();
        ProcessStartInfo startInfo = new(configuration.WebsockifyPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--run-once");
        startInfo.ArgumentList.Add($"127.0.0.1:{websockifyPort}");
        startInfo.ArgumentList.Add($"127.0.0.1:{vncPort}");
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start websockify.");
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        if (process.HasExited)
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            process.Dispose();
            throw new InvalidOperationException($"websockify exited before accepting a connection: {error.Trim()}");
        }

        string id = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.SessionLifetimeSeconds, 30, 1800));
        sessions[id] = new VncConsoleSession(id, expiresAt, websockifyPort, process);
        return id;
    }

    public async Task ProxyAsync(IAsyncStreamReader<VncProxyFrame> requestStream, IServerStreamWriter<VncProxyFrame> responseStream, CancellationToken cancellationToken)
    {
        if (!await requestStream.MoveNext(cancellationToken) || string.IsNullOrWhiteSpace(requestStream.Current.SessionId) || requestStream.Current.Data.Length != 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first VNC proxy frame must identify a session and contain no data."));
        }

        string sessionId = requestStream.Current.SessionId;
        if (!sessions.TryRemove(sessionId, out VncConsoleSession? session))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "VNC console session was not found."));
        }
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            DisposeSession(session);
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "VNC console session has expired."));
        }

        using ClientWebSocket socket = new();
        using CancellationTokenSource proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{session.WebsockifyPort}/"), proxyCancellation.Token);
            Task requestTask = CopyRequestsAsync(requestStream, socket, proxyCancellation.Token);
            Task responseTask = CopyResponsesAsync(sessionId, socket, responseStream, proxyCancellation.Token);
            await Task.WhenAny(requestTask, responseTask);
            proxyCancellation.Cancel();
            await IgnoreCancellationAsync(requestTask);
            await IgnoreCancellationAsync(responseTask);
        }
        finally
        {
            DisposeSession(session);
        }
    }

    public Task CleanupExpiredSessionsAsync()
    {
        foreach ((string id, VncConsoleSession session) in sessions)
        {
            if (session.ExpiresAt <= DateTimeOffset.UtcNow && sessions.TryRemove(id, out VncConsoleSession? expiredSession))
            {
                DisposeSession(expiredSession);
            }
        }
        return Task.CompletedTask;
    }

    private static async Task CopyRequestsAsync(IAsyncStreamReader<VncProxyFrame> requestStream, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken))
        {
            VncProxyFrame frame = requestStream.Current;
            if (!string.IsNullOrEmpty(frame.SessionId) || frame.Data.IsEmpty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VNC data frames must not include a session id and cannot be empty."));
            }
            await socket.SendAsync(frame.Data.Memory, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private static async Task CopyResponsesAsync(string sessionId, ClientWebSocket socket, IServerStreamWriter<VncProxyFrame> responseStream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[32 * 1024];
        using MemoryStream message = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new RpcException(new Status(StatusCode.Internal, "websockify returned a non-binary VNC frame."));
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            await responseStream.WriteAsync(new VncProxyFrame { SessionId = sessionId, Data = ByteString.CopyFrom(message.GetBuffer(), 0, checked((int)message.Length)) });
            message.SetLength(0);
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

    private static int ReserveLoopbackPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void DisposeSession(VncConsoleSession session)
    {
        if (!session.WebsockifyProcess.HasExited)
        {
            session.WebsockifyProcess.Kill(true);
        }
        session.WebsockifyProcess.Dispose();
    }
}
