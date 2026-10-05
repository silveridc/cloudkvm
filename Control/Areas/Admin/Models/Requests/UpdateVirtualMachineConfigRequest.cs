namespace Control.Areas.Admin.Models.Requests;

/// <summary>虚拟机配置更新请求：新 vCPU/内存值，null 表示不改。</summary>
public sealed record UpdateVirtualMachineConfigRequest(
    uint? VirtualCpuCount,
    ulong? MemoryMiB,
    bool ApplyLive = false);
