namespace Control.Model.Response;

public sealed record NatRuleResponse(
    string Id,
    string Protocol,
    string ListenAddress,
    uint ListenPort,
    string TargetAddress,
    uint TargetPort);
