namespace Cluster.Services;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
