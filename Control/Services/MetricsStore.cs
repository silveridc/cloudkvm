using System.Collections.Concurrent;
using Kvm.Contracts;

namespace Control.Services;

/// <summary>单节点的最后一次成功快照；LastSeenUtc 只在成功时推进，online/offline 由它的新旧程度决定。</summary>
public sealed record NodeMetricsEntry(DateTimeOffset? LastSeenUtc, NodeMetricsReply? Snapshot, string? LastError);

/// <summary>节点指标的最后成功值内存存储（线程安全）。</summary>
public sealed class MetricsStore
{
    private readonly ConcurrentDictionary<string, NodeMetricsEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void RecordSuccess(string node, NodeMetricsReply snapshot, DateTimeOffset timestampUtc)
    {
        _entries[node] = new NodeMetricsEntry(timestampUtc, snapshot, null);
    }

    public void RecordFailure(string node, string error)
    {
        _entries.AddOrUpdate(node, new NodeMetricsEntry(null, null, error), (_, existing) => existing with { LastError = error });
    }

    public NodeMetricsEntry? TryGet(string node)
    {
        return _entries.TryGetValue(node, out NodeMetricsEntry? entry) ? entry : null;
    }
}
