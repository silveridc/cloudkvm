namespace Cluster.Services;

/// <summary>虚拟机运行状态，对应 virsh dominfo 的 State 字段。</summary>
public enum VirshVirtualMachineState
{
    Unspecified,
    Running,
    Blocked,
    Paused,
    Shutdown,
    Shutoff,
    Crashed,
    PowerManagementSuspended
}
