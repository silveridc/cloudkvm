namespace Control.Model.Options;

public sealed class ClusterNodeOptions
{
    public string Address { get; set; } = string.Empty;
    public RpcOptions Rpc { get; set; } = new();
}
