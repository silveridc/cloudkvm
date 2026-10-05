namespace Cluster.Services;

/// <summary>cloud-init 配置：登录用户、SSH 公钥与静态 IPv4 网络参数。</summary>
public sealed record CloudInitConfiguration(
    string UserName,
    IReadOnlyList<string> SshAuthorizedKeys,
    string Ipv4Address,
    string Ipv4Gateway,
    IReadOnlyList<string> DnsServers,
    string SearchDomain
);
