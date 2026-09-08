namespace Cluster.Services;

public sealed record CloudInitConfiguration(
    string UserName,
    IReadOnlyList<string> SshAuthorizedKeys,
    string Ipv4Address,
    string Ipv4Gateway,
    IReadOnlyList<string> DnsServers,
    string SearchDomain);
