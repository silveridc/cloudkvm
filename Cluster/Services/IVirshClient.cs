namespace Cluster.Services;

public interface IVirshClient
{
    Task<VirshHostStatus> GetHostStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VirshVirtualMachine>> ListVirtualMachinesAsync(CancellationToken cancellationToken);
    Task<VirshVirtualMachine?> GetVirtualMachineAsync(string name, CancellationToken cancellationToken);
    Task<VirshVirtualMachine> ChangePowerStateAsync(string name, VirshPowerAction action, CancellationToken cancellationToken);
    Task DefineAsync(string domainXmlPath, CancellationToken cancellationToken);
    Task UndefineAsync(string name, CancellationToken cancellationToken);
    Task<int> GetVncPortAsync(string name, CancellationToken cancellationToken);
    Task<bool> IsManagedAsync(string name, CancellationToken cancellationToken);
    Task SetVncPasswordAsync(string name, string password, CancellationToken cancellationToken);
}
