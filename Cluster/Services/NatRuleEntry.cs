namespace Cluster.Services;

/// <summary>一条 NAT 规则在 nft 中的定位（链与句柄）及其内容。</summary>
internal sealed record NatRuleEntry(string Chain, int Handle, HostNatRule Rule);
