namespace Control.Model.Response;

/// <summary>网桥信息。</summary>
public sealed record BridgeResponse(string Name, string Type, IReadOnlyList<string> Ports, bool Up);
