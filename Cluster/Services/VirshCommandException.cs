namespace Cluster.Services;

public sealed class VirshCommandException(int exitCode, string standardError) : Exception($"virsh exited with code {exitCode}: {standardError}")
{
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}
