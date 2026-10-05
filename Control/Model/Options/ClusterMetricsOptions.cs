using System.ComponentModel.DataAnnotations;

namespace Control.Model.Options;

/// <summary>节点指标轮询配置。</summary>
public sealed class ClusterMetricsOptions
{
    public const string SectionName = "ClusterMetrics";

    [Range(5, 3600)]
    public int PollIntervalSeconds { get; set; } = 15;

    [Range(1, 64)]
    public int MaxConcurrentPolls { get; set; } = 4;

    [Range(1, 100)]
    public int OfflineAfterMissedPolls { get; set; } = 3;
}
