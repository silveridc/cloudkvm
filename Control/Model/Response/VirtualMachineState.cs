namespace Control.Model.Response;

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
