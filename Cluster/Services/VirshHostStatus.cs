namespace Cluster.Services;

public sealed record VirshHostStatus(string HostName, string LibvirtUri, string HypervisorType, string HypervisorVersion);
