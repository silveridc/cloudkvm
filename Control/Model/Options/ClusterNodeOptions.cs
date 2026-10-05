namespace Control.Model.Options;

/// <summary>单个集群节点的连接配置：地址、证书策略与 RPC 令牌。</summary>
public sealed class ClusterNodeOptions
{
    public string Address { get; set; } = string.Empty;
    public bool AllowSelfSignedCertificate { get; set; }
    public RpcOptions Rpc { get; set; } = new();
}
