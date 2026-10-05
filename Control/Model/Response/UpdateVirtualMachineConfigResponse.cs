namespace Control.Model.Response;

/// <summary>虚拟机配置更新结果：持久化与运行中各自是否生效。</summary>
public sealed record UpdateVirtualMachineConfigResponse(
    bool PersistentApplied,
    bool LiveApplied,
    string LiveMessage,
    uint PersistentVirtualCpuCount,
    ulong PersistentMemoryMiB,
    uint LiveVirtualCpuCount,
    ulong LiveMemoryMiB);
