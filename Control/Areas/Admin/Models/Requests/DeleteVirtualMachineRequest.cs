namespace Control.Areas.Admin.Models.Requests;

/// <summary>删除虚拟机请求：是否强制关机与是否删除存储。</summary>
public sealed record DeleteVirtualMachineRequest(bool Force, bool DeleteStorage = true);
