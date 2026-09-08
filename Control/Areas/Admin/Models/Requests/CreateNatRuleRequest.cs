namespace Control.Areas.Admin.Models.Requests;

public sealed record CreateNatRuleRequest(
    string Protocol,
    string ListenAddress,
    ushort ListenPort,
    string TargetAddress,
    ushort TargetPort);
