namespace Control.Areas.Admin.Models.Requests;

/// <summary>电源操作请求：start、shutdown、reboot 或 forceoff。</summary>
public sealed record ChangeVirtualMachinePowerStateRequest(string Action);
