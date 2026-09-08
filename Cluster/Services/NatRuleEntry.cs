namespace Cluster.Services;

internal sealed record NatRuleEntry(string Chain, int Handle, HostNatRule Rule);
