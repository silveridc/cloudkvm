namespace Control.Model.Options;

public sealed class ClustersOptions
{
    public const string SectionName = "Clusters";

    public Dictionary<string, ClusterNodeOptions> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
