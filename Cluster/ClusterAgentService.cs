using Cluster.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Kvm.Contracts;

namespace Cluster;

public sealed class ClusterAgentService(IVirshClient virshClient, IHostNetworkClient hostNetworkClient, IVirtualMachineProvisioner virtualMachineProvisioner, IVncConsoleService vncConsoleService, ILogger<ClusterAgentService> logger) : Kvm.Contracts.ClusterAgent.ClusterAgentBase
{
    public override async Task<HostStatusReply> GetHostStatus(HostStatusRequest request, ServerCallContext context)
    {
        try
        {
            VirshHostStatus status = await virshClient.GetHostStatusAsync(context.CancellationToken);
            return new HostStatusReply
            {
                HostName = status.HostName,
                LibvirtUri = status.LibvirtUri,
                HypervisorType = status.HypervisorType,
                HypervisorVersion = status.HypervisorVersion
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to query local libvirt host status.");
            throw new RpcException(new Status(StatusCode.Unavailable, exception.Message));
        }
    }

    public override async Task<ListVirtualMachinesReply> ListVirtualMachines(ListVirtualMachinesRequest request, ServerCallContext context)
    {
        IReadOnlyList<VirshVirtualMachine> virtualMachines = await virshClient.ListVirtualMachinesAsync(context.CancellationToken);
        ListVirtualMachinesReply reply = new();
        reply.VirtualMachines.AddRange(virtualMachines.Select(ToContract));
        return reply;
    }

    public override async Task<VirtualMachineReply> GetVirtualMachine(VirtualMachineRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        VirshVirtualMachine? virtualMachine = await virshClient.GetVirtualMachineAsync(request.Name, context.CancellationToken);

        if (virtualMachine is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Virtual machine was not found."));
        }
        return new VirtualMachineReply { VirtualMachine = ToContract(virtualMachine) };
    }

    public override async Task<ChangePowerStateReply> ChangeVirtualMachinePowerState(ChangePowerStateRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        VirshPowerAction action = request.Action switch
        {
            VirtualMachinePowerAction.Start => VirshPowerAction.Start,
            VirtualMachinePowerAction.Shutdown => VirshPowerAction.Shutdown,
            VirtualMachinePowerAction.Reboot => VirshPowerAction.Reboot,
            VirtualMachinePowerAction.ForceOff => VirshPowerAction.ForceOff,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "A supported power action is required."))
        };
        VirshVirtualMachine virtualMachine = await virshClient.ChangePowerStateAsync(request.Name, action, context.CancellationToken);
        return new ChangePowerStateReply { VirtualMachine = ToContract(virtualMachine) };
    }

    public override async Task<ListNetworksReply> ListNetworks(ListNetworksRequest request, ServerCallContext context)
    {
        IReadOnlyList<HostBridge> bridges = await hostNetworkClient.ListBridgesAsync(context.CancellationToken);
        ListNetworksReply reply = new();
        reply.Networks.AddRange(bridges.Select(ToContract));
        return reply;
    }

    public override async Task<NetworkReply> CreateBridge(CreateBridgeRequest request, ServerCallContext context)
    {
        HostBridgeType type = request.Type switch
        {
            BridgeType.Linux => HostBridgeType.Linux,
            BridgeType.OpenVswitch => HostBridgeType.OpenVSwitch,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "A supported bridge type is required."))
        };

        try
        {
            HostBridge bridge = await hostNetworkClient.CreateBridgeAsync(request.Name, type, request.Ports, context.CancellationToken);
            return new NetworkReply { Network = ToContract(bridge) };
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
    }

    public override async Task<Empty> DeleteBridge(DeleteBridgeRequest request, ServerCallContext context)
    {
        HostBridgeType type = request.Type switch
        {
            BridgeType.Linux => HostBridgeType.Linux,
            BridgeType.OpenVswitch => HostBridgeType.OpenVSwitch,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "A supported bridge type is required."))
        };

        try
        {
            await hostNetworkClient.DeleteBridgeAsync(request.Name, type, context.CancellationToken);
            return new Empty();
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
    }

    public override async Task<ListNatRulesReply> ListNatRules(ListNatRulesRequest request, ServerCallContext context)
    {
        IReadOnlyList<HostNatRule> rules = await hostNetworkClient.ListNatRulesAsync(context.CancellationToken);
        ListNatRulesReply reply = new();
        reply.Rules.AddRange(rules.Select(ToContract));
        return reply;
    }

    public override async Task<NatRuleReply> CreateNatRule(CreateNatRuleRequest request, ServerCallContext context)
    {
        try
        {
            HostNatRule rule = await hostNetworkClient.CreateNatRuleAsync(
                request.Protocol.ToLowerInvariant(),
                request.ListenAddress,
                ToPort(request.ListenPort, nameof(request.ListenPort)),
                request.TargetAddress,
                ToPort(request.TargetPort, nameof(request.TargetPort)),
                context.CancellationToken);
            return new NatRuleReply { Rule = ToContract(rule) };
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
    }

    public override async Task<Empty> DeleteNatRule(DeleteNatRuleRequest request, ServerCallContext context)
    {
        try
        {
            await hostNetworkClient.DeleteNatRuleAsync(request.Id, context.CancellationToken);
            return new Empty();
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
    }

    public override async Task<VirtualMachineReply> CreateVirtualMachine(CreateVirtualMachineRequest request, ServerCallContext context)
    {
        try
        {
            VirshVirtualMachine virtualMachine = await virtualMachineProvisioner.CreateAsync(
                new VirtualMachineProvisionRequest(
                    request.Name,
                    request.MemoryMib,
                    request.VirtualCpuCount,
                    request.BaseImage,
                    request.BridgeName,
                    request.MacAddress,
                    new CloudInitConfiguration(
                        request.CloudInit?.UserName ?? string.Empty,
                        request.CloudInit?.SshAuthorizedKeys ?? [],
                        request.CloudInit?.Ipv4Address ?? string.Empty,
                        request.CloudInit?.Ipv4Gateway ?? string.Empty,
                        request.CloudInit?.DnsServers ?? [],
                        request.CloudInit?.SearchDomain ?? string.Empty),
                    request.Start),
                context.CancellationToken);
            return new VirtualMachineReply { VirtualMachine = ToContract(virtualMachine) };
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    public override async Task<Empty> DeleteVirtualMachine(DeleteVirtualMachineRequest request, ServerCallContext context)
    {
        try
        {
            await virtualMachineProvisioner.DeleteAsync(request.Name, request.Force, request.DeleteStorage, context.CancellationToken);
            return new Empty();
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    public override async Task<OpenVncConsoleReply> OpenVncConsole(OpenVncConsoleRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        try
        {
            string sessionId = await vncConsoleService.OpenAsync(request.Name, context.CancellationToken);
            return new OpenVncConsoleReply { SessionId = sessionId };
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    public override Task ProxyVnc(IAsyncStreamReader<VncProxyFrame> requestStream, IServerStreamWriter<VncProxyFrame> responseStream, ServerCallContext context)
    {
        return vncConsoleService.ProxyAsync(requestStream, responseStream, context.CancellationToken);
    }

    private static Network ToContract(HostBridge source)
    {
        Network network = new()
        {
            Name = source.Name,
            Type = source.Type == HostBridgeType.Linux ? BridgeType.Linux : BridgeType.OpenVswitch,
            Up = source.Up
        };
        network.Ports.AddRange(source.Ports);
        return network;
    }

    private static NatRule ToContract(HostNatRule source) => new()
    {
        Id = source.Id,
        Protocol = source.Protocol,
        ListenAddress = source.ListenAddress,
        ListenPort = source.ListenPort,
        TargetAddress = source.TargetAddress,
        TargetPort = source.TargetPort
    };

    private static ushort ToPort(uint port, string parameterName)
    {
        if (port is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("A port from 1 through 65535 is required.", parameterName);
        }

        return (ushort)port;
    }

    private static VirtualMachine ToContract(VirshVirtualMachine source)
    {
        return new VirtualMachine
        {
            Name = source.Name,
            Uuid = source.Uuid,
            Id = source.Id,
            State = source.State switch
            {
                VirshVirtualMachineState.Running => VirtualMachineState.Running,
                VirshVirtualMachineState.Blocked => VirtualMachineState.Blocked,
                VirshVirtualMachineState.Paused => VirtualMachineState.Paused,
                VirshVirtualMachineState.Shutdown => VirtualMachineState.Shutdown,
                VirshVirtualMachineState.Shutoff => VirtualMachineState.Shutoff,
                VirshVirtualMachineState.Crashed => VirtualMachineState.Crashed,
                VirshVirtualMachineState.PowerManagementSuspended => VirtualMachineState.Pmsuspended,
                _ => VirtualMachineState.Unspecified
            },
            MemoryMib = source.MemoryMiB,
            VirtualCpuCount = source.VirtualCpuCount,
            Persistent = source.Persistent
        };
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A virtual machine name is required and must be at most 255 characters."));
        }
    }
}
