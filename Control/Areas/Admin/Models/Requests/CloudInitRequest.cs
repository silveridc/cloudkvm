namespace Control.Areas.Admin.Models.Requests;

/// <summary>创建虚拟机时的 cloud-init 参数。</summary>
public sealed record CloudInitRequest(
    string UserName,
    IReadOnlyList<string> SshAuthorizedKeys,
    string Ipv4Address,
    string? Ipv4Gateway,
    IReadOnlyList<string>? DnsServers,
    string? SearchDomain);
