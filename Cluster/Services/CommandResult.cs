namespace Cluster.Services;

/// <summary>一次外部命令的退出码与标准输出、标准错误。</summary>
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
