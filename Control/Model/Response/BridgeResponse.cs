namespace Control.Model.Response;

public sealed record BridgeResponse(string Name, string Type, IReadOnlyList<string> Ports, bool Up);
