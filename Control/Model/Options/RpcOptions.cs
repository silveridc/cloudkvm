namespace Control.Model.Options;

/// <summary>集群 RPC 认证令牌，内嵌在 ClusterNodeOptions 下。</summary>
public sealed class RpcOptions
{
    public string Token { get; set; } = string.Empty;
}
