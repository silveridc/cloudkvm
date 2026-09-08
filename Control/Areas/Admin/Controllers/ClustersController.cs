using Control.Areas.Admin.Models.Requests;
using Control.Model.Response;
using Control.Services;
using Grpc.Core;
using Kvm.Contracts;

namespace Control.Areas.Admin.Controllers;

[Area("Admin")]
[ApiController]
[Route("/admin/v1/clusters")]
public sealed class ClustersController(
    IClusterClientFactory clusterClientFactory,
    OperationQueue operationQueue,
    VncConsoleTicketStore vncConsoleTicketStore) : AdminControllerBase
{
    [HttpGet("{node}")]
    [ProducesResponseType<Response<ClusterHostStatusResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHostStatus(string node, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            HostStatusReply reply = await client.GetHostStatusAsync(new HostStatusRequest(), cancellationToken: cancellationToken);
            return Success(new ClusterHostStatusResponse(node, reply.HostName, reply.LibvirtUri, reply.HypervisorType, reply.HypervisorVersion));
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpGet("{node}/vm")]
    [ProducesResponseType<Response<IReadOnlyList<VirtualMachineResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListVirtualMachines(string node, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            ListVirtualMachinesReply reply = await client.ListVirtualMachinesAsync(new ListVirtualMachinesRequest(), cancellationToken: cancellationToken);
            return Success(reply.VirtualMachines.Select(ToResponse).ToArray());
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpGet("{node}/vm/{name}")]
    [ProducesResponseType<Response<VirtualMachineResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVirtualMachine(string node, string name, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            VirtualMachineReply reply = await client.GetVirtualMachineAsync(new VirtualMachineRequest { Name = name }, cancellationToken: cancellationToken);
            return Success(ToResponse(reply.VirtualMachine));
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return NotFoundResponse();
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.InvalidArgument)
        {
            return ValidationError("name", exception.Status.Detail);
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpPost("{node}/vm/{name}/power")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ChangePowerState(string node, string name, ChangeVirtualMachinePowerStateRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetPowerAction(request.Action, out VirtualMachinePowerAction action))
        {
            return ValidationError("action", Resources.Localization.API.invalid_power_action);
        }
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        return await EnqueueOperationAsync(
            operationQueue,
            "vm.power",
            node,
            new { name, request },
            async cancellationToken =>
            {
                ChangePowerStateReply reply = await client.ChangeVirtualMachinePowerStateAsync(
                    new ChangePowerStateRequest { Name = name, Action = action },
                    cancellationToken: cancellationToken);
                return ToResponse(reply.VirtualMachine);
            }, cancellationToken);
    }

    [HttpPost("{node}/vm")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateVirtualMachine(string node, Control.Areas.Admin.Models.Requests.CreateVirtualMachineRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.MemoryMiB < 512 || request.VirtualCpuCount == 0 || request.CloudInit is null)
        {
            return ValidationError("virtualMachine", Resources.Localization.API.invalid_value);
        }
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        Kvm.Contracts.CreateVirtualMachineRequest clusterRequest = new()
        {
            Name = request.Name,
            MemoryMib = request.MemoryMiB,
            VirtualCpuCount = request.VirtualCpuCount,
            BaseImage = request.BaseImage,
            BridgeName = request.BridgeName,
            MacAddress = request.MacAddress,
            CloudInit = new CloudInitConfig
            {
                UserName = request.CloudInit.UserName,
                Ipv4Address = request.CloudInit.Ipv4Address,
                Ipv4Gateway = request.CloudInit.Ipv4Gateway ?? string.Empty,
                SearchDomain = request.CloudInit.SearchDomain ?? string.Empty
            },
            Start = request.Start
        };
        clusterRequest.CloudInit.SshAuthorizedKeys.AddRange(request.CloudInit.SshAuthorizedKeys);
        clusterRequest.CloudInit.DnsServers.AddRange(request.CloudInit.DnsServers ?? []);

        return await EnqueueOperationAsync(
            operationQueue,
            "vm.create",
            node,
            request,
            async cancellationToken =>
            {
                VirtualMachineReply reply = await client.CreateVirtualMachineAsync(clusterRequest, cancellationToken: cancellationToken);
                return ToResponse(reply.VirtualMachine);
            }, cancellationToken);
    }

    [HttpDelete("{node}/vm/{name}")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> DeleteVirtualMachine(string node, string name, [FromQuery] bool force = false, [FromQuery] bool deleteStorage = true, CancellationToken cancellationToken = default)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        Control.Areas.Admin.Models.Requests.DeleteVirtualMachineRequest request = new(force, deleteStorage);
        return await EnqueueOperationAsync(
            operationQueue,
            "vm.delete",
            node,
            new { name, request },
            async cancellationToken =>
            {
                await client.DeleteVirtualMachineAsync(
                    new Kvm.Contracts.DeleteVirtualMachineRequest
                    {
                        Name = name,
                        Force = force,
                        DeleteStorage = deleteStorage
                    },
                    cancellationToken: cancellationToken);
                return null;
            }, cancellationToken);
    }

    [HttpPost("{node}/vm/{name}/console")]
    [ProducesResponseType<Response<VncConsoleResponse>>(StatusCodes.Status201Created)]
    public async Task<IActionResult> OpenVncConsole(string node, string name, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            OpenVncConsoleReply reply = await client.OpenVncConsoleAsync(new OpenVncConsoleRequest { Name = name }, cancellationToken: cancellationToken);
            VncTicketCreation ticket = await vncConsoleTicketStore.CreateAsync(node, name, reply.SessionId, cancellationToken);
            return Success(new VncConsoleResponse($"/noVNC/kvm-console.html?ticket={ticket.Token}", ticket.ExpiresAt), StatusCodes.Status201Created);
        }
        catch (RpcException exception) when (exception.StatusCode is Grpc.Core.StatusCode.InvalidArgument or Grpc.Core.StatusCode.FailedPrecondition)
        {
            return ValidationError("console", exception.Status.Detail);
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    private static bool TryGetPowerAction(string action, out VirtualMachinePowerAction powerAction)
    {
        powerAction = action.ToLowerInvariant() switch
        {
            "start" => VirtualMachinePowerAction.Start,
            "shutdown" => VirtualMachinePowerAction.Shutdown,
            "reboot" => VirtualMachinePowerAction.Reboot,
            "force-off" => VirtualMachinePowerAction.ForceOff,
            _ => VirtualMachinePowerAction.Unspecified
        };
        return powerAction != VirtualMachinePowerAction.Unspecified;
    }

    private static VirtualMachineResponse ToResponse(VirtualMachine source) => new(
        source.Name,
        source.Uuid,
        source.Id,
        source.State switch
        {
            Kvm.Contracts.VirtualMachineState.Running => Model.Response.VirtualMachineState.Running,
            Kvm.Contracts.VirtualMachineState.Blocked => Model.Response.VirtualMachineState.Blocked,
            Kvm.Contracts.VirtualMachineState.Paused => Model.Response.VirtualMachineState.Paused,
            Kvm.Contracts.VirtualMachineState.Shutdown => Model.Response.VirtualMachineState.Shutdown,
            Kvm.Contracts.VirtualMachineState.Shutoff => Model.Response.VirtualMachineState.Shutoff,
            Kvm.Contracts.VirtualMachineState.Crashed => Model.Response.VirtualMachineState.Crashed,
            Kvm.Contracts.VirtualMachineState.Pmsuspended => Model.Response.VirtualMachineState.PowerManagementSuspended,
            _ => Model.Response.VirtualMachineState.Unspecified
        },
        source.MemoryMib,
        source.VirtualCpuCount,
        source.Persistent);
}
