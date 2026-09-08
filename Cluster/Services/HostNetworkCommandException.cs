namespace Cluster.Services;

public sealed class HostNetworkCommandException(string command, int exitCode, string standardError) : Exception($"{command} exited with code {exitCode}: {standardError}")
{
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}
