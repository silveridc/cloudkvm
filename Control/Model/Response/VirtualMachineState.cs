namespace Control.Model.Response;

/// <summary>虚拟机运行状态。</summary>
public enum VirtualMachineState
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
