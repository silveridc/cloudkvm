using Cluster.Services;

namespace Cluster.Interface;

public interface IVirtualMachineProvisioner
{
    Task<VirshVirtualMachine> CreateAsync(VirtualMachineProvisionRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(string name, bool force, bool deleteStorage, CancellationToken cancellationToken);
}
