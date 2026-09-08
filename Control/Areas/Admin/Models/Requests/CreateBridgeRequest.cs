namespace Control.Areas.Admin.Models.Requests;

public sealed record CreateBridgeRequest(string Name, string Type, IReadOnlyList<string>? Ports);
