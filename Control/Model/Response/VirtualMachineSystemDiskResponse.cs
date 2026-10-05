namespace Control.Model.Response;

/// <summary>系统盘信息：相对文件名与容量。</summary>
public sealed record VirtualMachineSystemDiskResponse(string File, ulong SizeGiB);
