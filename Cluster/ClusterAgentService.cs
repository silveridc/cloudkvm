using Cluster.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Kvm.Contracts;
using System.Text.RegularExpressions;
using Cluster.Interface;

namespace Cluster;

/// <summary>集群代理的 gRPC 服务实现，把宿主机与虚拟机操作暴露给 Control 节点。</summary>
public sealed class ClusterAgentService(IVirshClient virshClient, IHostNetworkClient hostNetworkClient, IVirtualMachineProvisioner virtualMachineProvisioner, IVirtualMachineConfigurationManager virtualMachineConfigurationManager, IVncConsoleService vncConsoleService, INodeMetricsCollector nodeMetricsCollector, ILogger<ClusterAgentService> logger) : Kvm.Contracts.ClusterAgent.ClusterAgentBase
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
            throw new RpcException(new Status(StatusCode.Unavailable, "The libvirt service is unavailable."));
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
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
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
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
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
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
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
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
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
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cluster operation precondition failed.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The cluster resource is not in the required state."));
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
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cluster operation precondition failed.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The cluster resource is not in the required state."));
        }
    }

    public override async Task<VirtualMachineConfigReply> GetVirtualMachineConfig(VirtualMachineRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        try
        {
            VirshVirtualMachineConfig config = await virtualMachineConfigurationManager.GetConfigAsync(request.Name, context.CancellationToken);
            return new VirtualMachineConfigReply
            {
                Config = new VirtualMachineConfig
                {
                    Name = config.Name,
                    State = ToContractState(config.State),
                    Running = config.Running,
                    PersistentVirtualCpuCount = config.PersistentVirtualCpuCount,
                    PersistentMemoryMib = config.PersistentMemoryMiB,
                    LiveVirtualCpuCount = config.LiveVirtualCpuCount,
                    LiveMemoryMib = config.LiveMemoryMiB,
                    SystemDisk = ToContract(config.SystemDisk)
                }
            };
        }
        catch (VirtualMachineNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Virtual machine was not found."));
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cluster operation precondition failed.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The cluster resource is not in the required state."));
        }
    }

    public override async Task<UpdateVirtualMachineConfigReply> UpdateVirtualMachineConfig(UpdateVirtualMachineConfigRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        uint? virtualCpuCount = request.HasVirtualCpuCount ? request.VirtualCpuCount : null;
        ulong? memoryMiB = request.HasMemoryMib ? request.MemoryMib : null;
        try
        {
            VirshVirtualMachineConfigUpdate update = await virtualMachineConfigurationManager.UpdateConfigAsync(
                request.Name,
                virtualCpuCount,
                memoryMiB,
                request.ApplyLive,
                context.CancellationToken);
            return new UpdateVirtualMachineConfigReply
            {
                PersistentApplied = update.PersistentApplied,
                LiveApplied = update.LiveApplied,
                LiveMessage = update.LiveMessage,
                PersistentVirtualCpuCount = update.PersistentVirtualCpuCount,
                PersistentMemoryMib = update.PersistentMemoryMiB,
                LiveVirtualCpuCount = update.LiveVirtualCpuCount,
                LiveMemoryMib = update.LiveMemoryMiB
            };
        }
        catch (VirtualMachineNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Virtual machine was not found."));
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cluster operation precondition failed.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The cluster resource is not in the required state."));
        }
    }

    public override async Task<ResizeVirtualMachineDiskReply> ResizeVirtualMachineDisk(ResizeVirtualMachineDiskRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        try
        {
            VirshVirtualMachineSystemDisk systemDisk = await virtualMachineConfigurationManager.ResizeSystemDiskAsync(
                request.Name,
                request.SizeGib,
                context.CancellationToken);
            return new ResizeVirtualMachineDiskReply { SystemDisk = ToContract(systemDisk) };
        }
        catch (VirtualMachineNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Virtual machine was not found."));
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning(exception, "Invalid cluster request parameters.");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request parameters are invalid."));
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cluster operation precondition failed.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The cluster resource is not in the required state."));
        }
    }

    public override async Task<OpenVncConsoleReply> OpenVncConsole(OpenVncConsoleRequest request, ServerCallContext context)
    {
        ValidateName(request.Name);
        try
        {
            VncConsoleOpenResult result = await vncConsoleService.OpenAsync(request.Name, context.CancellationToken);
            return new OpenVncConsoleReply { SessionId = result.SessionId, Password = result.Password };
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Cluster operation precondition failed.");
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The cluster resource is not in the required state."));
        }
    }

    public override Task ProxyVnc(IAsyncStreamReader<VncProxyFrame> requestStream, IServerStreamWriter<VncProxyFrame> responseStream, ServerCallContext context)
    {
        return vncConsoleService.ProxyAsync(requestStream, responseStream, context.CancellationToken);
    }

    public override async Task<NodeMetricsReply> GetNodeMetrics(NodeMetricsRequest request, ServerCallContext context)
    {
        try
        {
            // 采集器对测不到的字段留空，只有取消或意外失败才变成 RPC 错误。
            return await nodeMetricsCollector.GetNodeMetricsAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to collect node metrics.");
            throw new RpcException(new Status(StatusCode.Unavailable, "Node metrics are unavailable."));
        }
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

    private static VirtualMachineSystemDisk ToContract(VirshVirtualMachineSystemDisk source)
    {
        return new VirtualMachineSystemDisk
        {
            File = source.File,
            SizeGib = source.SizeGiB
        };
    }

    private static VirtualMachineState ToContractState(VirshVirtualMachineState state)
    {
        return state switch
        {
            VirshVirtualMachineState.Running => VirtualMachineState.Running,
            VirshVirtualMachineState.Blocked => VirtualMachineState.Blocked,
            VirshVirtualMachineState.Paused => VirtualMachineState.Paused,
            VirshVirtualMachineState.Shutdown => VirtualMachineState.Shutdown,
            VirshVirtualMachineState.Shutoff => VirtualMachineState.Shutoff,
            VirshVirtualMachineState.Crashed => VirtualMachineState.Crashed,
            VirshVirtualMachineState.PowerManagementSuspended => VirtualMachineState.Pmsuspended,
            _ => VirtualMachineState.Unspecified
        };
    }

    private static VirtualMachine ToContract(VirshVirtualMachine source)
    {
        return new VirtualMachine
        {
            Name = source.Name,
            Uuid = source.Uuid,
            Id = source.Id,
            State = ToContractState(source.State),
            MemoryMib = source.MemoryMiB,
            VirtualCpuCount = source.VirtualCpuCount,
            Persistent = source.Persistent
        };
    }

    private static readonly Regex _virtualMachineNamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ValidateName(string name)
    {
        if (!_virtualMachineNamePattern.IsMatch(name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Virtual machine name is invalid."));
        }
    }
}
