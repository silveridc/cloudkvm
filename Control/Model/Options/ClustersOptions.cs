namespace Control.Model.Options;

/// <summary>集群节点清单（Clusters 配置节）。</summary>
public sealed class ClustersOptions
{
    public const string SectionName = "Clusters";

    public Dictionary<string, ClusterNodeOptions> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
