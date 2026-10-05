namespace Control.Areas.Admin.Models.Requests;

/// <summary>系统盘扩容请求：目标容量（GiB，只增不减）。</summary>
public sealed record ResizeVirtualMachineDiskRequest(ulong SizeGiB);
