namespace Control.Areas.Admin.Models.Requests;

/// <summary>创建网桥请求：名称、类型与挂接的物理端口。</summary>
public sealed record CreateBridgeRequest(string Name, string Type, IReadOnlyList<string>? Ports);
