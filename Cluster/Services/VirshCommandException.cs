namespace Cluster.Services;

/// <summary>virsh/qemu-img 命令执行失败。</summary>
public sealed class VirshCommandException(int exitCode, string standardError) : Exception($"command exited with code {exitCode}: {standardError}")
{
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}
