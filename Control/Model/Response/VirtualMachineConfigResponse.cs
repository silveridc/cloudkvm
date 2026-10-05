namespace Control.Model.Response;

/// <summary>虚拟机配置：持久值来自域定义（dumpxml --inactive），运行值来自 dominfo，关机时待下次开机生效；系统盘只报相对文件名，不暴露宿主机路径。</summary>
public sealed record VirtualMachineConfigResponse(
    string Name,
    VirtualMachineState State,
    bool Running,
    uint PersistentVirtualCpuCount,
    ulong PersistentMemoryMiB,
    uint LiveVirtualCpuCount,
    ulong LiveMemoryMiB,
    VirtualMachineSystemDiskResponse SystemDisk);
