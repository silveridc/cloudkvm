namespace Control.Areas.Admin.Models.Requests;

public sealed record CloudInitRequest(
    string UserName,
    IReadOnlyList<string> SshAuthorizedKeys,
    string Ipv4Address,
    string? Ipv4Gateway,
    IReadOnlyList<string>? DnsServers,
    string? SearchDomain);
