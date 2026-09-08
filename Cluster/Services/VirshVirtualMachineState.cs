namespace Cluster.Services;

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
