namespace Control.Areas.Admin.Models.Requests;

/// <summary>创建 NAT 转发规则请求。</summary>
public sealed record CreateNatRuleRequest(
    string Protocol,
    string ListenAddress,
    ushort ListenPort,
    string TargetAddress,
    ushort TargetPort);
