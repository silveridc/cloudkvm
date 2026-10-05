namespace Cluster.Services;

/// <summary>宿主机基本信息：主机名、libvirt 连接地址与虚拟化层版本。</summary>
public sealed record VirshHostStatus(string HostName, string LibvirtUri, string HypervisorType, string HypervisorVersion);
