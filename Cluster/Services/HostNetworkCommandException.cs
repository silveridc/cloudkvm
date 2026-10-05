namespace Cluster.Services;

/// <summary>宿主机网络命令执行失败。</summary>
public sealed class HostNetworkCommandException(string command, int exitCode, string standardError) : Exception($"{command} exited with code {exitCode}: {standardError}")
{
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}
