namespace Control.Model;

/// <summary>VNC 控制台一次性票据：目标节点、虚拟机与所属集群会话。</summary>
public sealed record VncConsoleTicket(string Node, string VirtualMachineName, string ClusterSessionId, DateTimeOffset ExpiresAt);
