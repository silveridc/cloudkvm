namespace Cluster.Services;

/// <summary>域定义中的一块磁盘。</summary>
public sealed record VirshDomainDisk(string Device, string TargetDevice, string? SourceFile, string? DriverType);

/// <summary>从 dumpxml 解析出的域持久定义。</summary>
public sealed record VirshDomainDefinition(
    string Uuid,
    ulong MaximumMemoryKiB,
    ulong CurrentMemoryKiB,
    uint VirtualCpuCount,
    IReadOnlyList<VirshDomainDisk> Disks);

/// <summary>原始域 XML 与其解析结果。</summary>
public sealed record VirshDomainXml(string Xml, VirshDomainDefinition Definition);
