using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Kvm.Contracts;
using Cluster.Interface;

namespace Cluster.Services;

/// <summary>VNC 控制台服务：经 websockify 把浏览器接入受管 VM 的 VNC 端口，每台 VM 同一会话且数量有上限。</summary>
public sealed class VncConsoleService(IOptions<Models.Options.VncOptions> options, IVirshClient virshClient) : IVncConsoleService
{
    private const int MaximumFrameBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<string, VncConsoleSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _virtualMachineSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionSlots = new(Math.Clamp(options.Value.MaximumSessions, 1, 256));

    public async Task<VncConsoleOpenResult> OpenAsync(string name, CancellationToken cancellationToken)
    {
        VirshVirtualMachine? virtualMachine = await virshClient.GetVirtualMachineAsync(name, cancellationToken);
        if (virtualMachine is null)
        {
            throw new InvalidOperationException("Virtual machine was not found or is not managed by KvmControl.");
        }
        if (virtualMachine.State != VirshVirtualMachineState.Running)
        {
            throw new InvalidOperationException("Virtual machine must be running before opening a VNC console.");
        }
        if (!await virshClient.IsManagedAsync(name, cancellationToken))
        {
            throw new InvalidOperationException("Virtual machine is not managed by KvmControl.");
        }

        Models.Options.VncOptions configuration = options.Value;
        if (!await _sessionSlots.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("The VNC session limit has been reached.");
        }

        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        if (!_virtualMachineSessions.TryAdd(name, id))
        {
            _sessionSlots.Release();
            throw new InvalidOperationException("A VNC console session already exists for this virtual machine.");
        }

        Process? process = null;
        string? tokenFilePath = null;
        bool passwordSet = false;
        bool sessionCommitted = false;
        try
        {
            if (!Path.IsPathFullyQualified(configuration.WebsockifyPath) || !File.Exists(configuration.WebsockifyPath))
            {
                throw new InvalidOperationException("Configured websockify binary is not available.");
            }

            TimeSpan lifetime = TimeSpan.FromSeconds(Math.Clamp(configuration.SessionLifetimeSeconds, 30, 1800));
            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(6));
            await virshClient.SetVncPasswordAsync(name, password, lifetime, cancellationToken);
            passwordSet = true;
            int vncPort = await virshClient.GetVncPortAsync(name, cancellationToken);
            int websockifyPort = ReserveLoopbackPort();
            tokenFilePath = Path.Combine(Path.GetTempPath(), $"kvmcontrol-vnc-{Guid.NewGuid():N}.tokens");
            await File.WriteAllTextAsync(tokenFilePath, $"{id}: 127.0.0.1:{vncPort}\n", cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tokenFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            ProcessStartInfo startInfo = new(configuration.WebsockifyPath)
            {
                RedirectStandardError = false,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--run-once");
            startInfo.ArgumentList.Add("--token-plugin");
            startInfo.ArgumentList.Add("TokenFile");
            startInfo.ArgumentList.Add($"--token-source={tokenFilePath}");
            startInfo.ArgumentList.Add($"127.0.0.1:{websockifyPort}");
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start websockify.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            if (process.HasExited)
            {
                throw new InvalidOperationException("websockify exited before accepting a connection.");
            }

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
            VncConsoleSession session = new(id, name, expiresAt, websockifyPort, tokenFilePath!, process);
            if (!_sessions.TryAdd(id, session))
            {
                throw new InvalidOperationException("Unable to register the VNC console session.");
            }
            process = null;
            sessionCommitted = true;
            return new VncConsoleOpenResult(id, password);
        }
        finally
        {
            if (!sessionCommitted)
            {
                if (process is not null)
                {
                    DisposeProcess(process);
                }
                if (tokenFilePath is not null)
                {
                    DeleteTokenFile(tokenFilePath);
                }
                if (passwordSet)
                {
                    await ExpirePasswordSafelyAsync(name);
                }
                _virtualMachineSessions.TryRemove(new KeyValuePair<string, string>(name, id));
                _sessionSlots.Release();
            }
        }
    }

    public async Task ProxyAsync(IAsyncStreamReader<VncProxyFrame> requestStream, IServerStreamWriter<VncProxyFrame> responseStream, CancellationToken cancellationToken)
    {
        if (!await requestStream.MoveNext(cancellationToken) || string.IsNullOrWhiteSpace(requestStream.Current.SessionId) || requestStream.Current.Data.Length != 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first VNC proxy frame must identify a session and contain no data."));
        }

        string sessionId = requestStream.Current.SessionId;
        if (!_sessions.TryRemove(sessionId, out VncConsoleSession? session))
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
        proxyCancellation.CancelAfter(session.ExpiresAt - DateTimeOffset.UtcNow);
        try
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{session.WebsockifyPort}/websockify?token={Uri.EscapeDataString(session.Id)}"), proxyCancellation.Token);
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

    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        foreach ((string id, VncConsoleSession session) in _sessions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (session.ExpiresAt <= DateTimeOffset.UtcNow && _sessions.TryRemove(id, out VncConsoleSession? expiredSession))
            {
                await DisposeSessionAsync(expiredSession);
            }
        }
    }

    private static async Task CopyRequestsAsync(IAsyncStreamReader<VncProxyFrame> requestStream, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken))
        {
            VncProxyFrame frame = requestStream.Current;
            if (!string.IsNullOrEmpty(frame.SessionId) || frame.Data.IsEmpty || frame.Data.Length > MaximumFrameBytes)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "VNC data frames are invalid or too large."));
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

            if (message.Length + result.Count > MaximumFrameBytes)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "VNC frame exceeded the maximum size."));
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

    private void DisposeSession(VncConsoleSession session)
    {
        DisposeProcess(session.WebsockifyProcess);
        DeleteTokenFile(session.TokenFilePath);
        ExpirePasswordSafelyAsync(session.VirtualMachineName).GetAwaiter().GetResult();
        ReleaseSessionSlot(session);
    }

    private async Task DisposeSessionAsync(VncConsoleSession session)
    {
        DisposeProcess(session.WebsockifyProcess);
        DeleteTokenFile(session.TokenFilePath);
        await ExpirePasswordSafelyAsync(session.VirtualMachineName);
        ReleaseSessionSlot(session);
    }

    private void ReleaseSessionSlot(VncConsoleSession session)
    {
        if (_virtualMachineSessions.TryRemove(new KeyValuePair<string, string>(session.VirtualMachineName, session.Id)))
        {
            _sessionSlots.Release();
        }
    }

    private static void DeleteTokenFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task ExpirePasswordSafelyAsync(string name)
    {
        try
        {
            await virshClient.ExpireVncPasswordAsync(name, CancellationToken.None);
        }
        catch
        {
        }
    }

    private static void DisposeProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
            process.WaitForExit();
        }
        process.Dispose();
    }
}
