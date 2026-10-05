using System.ComponentModel.DataAnnotations;

namespace Cluster.Models.Options;

/// <summary>集群 RPC 认证令牌配置。</summary>
public sealed class RpcOptions
{
    public const string SectionName = "Rpc";

    [Required]
    public string Token { get; init; } = string.Empty;
}
