namespace Control.Model.Response;

/// <summary>NAT 规则条目。</summary>
public sealed record NatRuleResponse(
    string Id,
    string Protocol,
    string ListenAddress,
    uint ListenPort,
    string TargetAddress,
    uint TargetPort);
