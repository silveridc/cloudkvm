namespace Control.Model;

public sealed record VncConsoleTicket(string Node, string VirtualMachineName, string ClusterSessionId, DateTimeOffset ExpiresAt);
