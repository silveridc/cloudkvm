using Control.Areas.Admin.Models.Requests;
using Control.Model.Response;
using Control.Services;
using Grpc.Core;
using Kvm.Contracts;
using Control.Interface;

namespace Control.Areas.Admin.Controllers;

[Area("Admin")]
[ApiController]
[Route("/admin/v1/clusters")]
/// <summary>集群节点管理端点：节点列表与状态、虚拟机 CRUD、配置与磁盘、电源与 VNC。</summary>
public sealed class ClustersController(
    IClusterClientFactory clusterClientFactory,
    OperationQueue operationQueue,
    VncConsoleTicketStore vncConsoleTicketStore,
    MetricsStore metricsStore,
    IOptionsMonitor<ClustersOptions> clustersOptions,
    IOptionsMonitor<ClusterMetricsOptions> metricsOptions) : AdminControllerBase
{
    [HttpGet]
    [ProducesResponseType<Response<IReadOnlyList<ClusterNodeSummaryResponse>>>(StatusCodes.Status200OK)]
    public IActionResult ListClusters()
    {
        // 只读后台指标存储，不向节点发起实时请求。
        ClusterMetricsOptions options = metricsOptions.CurrentValue;
        TimeSpan pollInterval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        List<ClusterNodeSummaryResponse> nodes = clustersOptions.CurrentValue.Nodes
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                NodeMetricsEntry? entry = metricsStore.TryGet(pair.Key);
                return new ClusterNodeSummaryResponse(
                    pair.Key,
                    MetricsOnlineEvaluator.IsOnline(entry?.LastSeenUtc, nowUtc, pollInterval, options.OfflineAfterMissedPolls),
                    entry?.LastSeenUtc,
                    ToHostMetricsResponse(entry),
                    ToVirtualMachineSummary(entry));
            })
            .ToList();
        return Success(nodes);
    }

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
            return Success(new ClusterHostStatusResponse(node, reply.HostName, reply.LibvirtUri, reply.HypervisorType, reply.HypervisorVersion)
            {
                Metrics = ToHostMetricsResponse(metricsStore.TryGet(node))
            });
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
            return Success(ToResponse(reply.VirtualMachine) with { Metrics = ToVirtualMachineMetricsResponse(metricsStore.TryGet(node), name) });
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return NotFoundResponse();
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.InvalidArgument)
        {
            return ValidationError("name", Resources.Localization.API.invalid_value);
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

    [HttpGet("{node}/vm/{name}/config")]
    [ProducesResponseType<Response<VirtualMachineConfigResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVirtualMachineConfig(string node, string name, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            VirtualMachineConfigReply reply = await client.GetVirtualMachineConfigAsync(new VirtualMachineRequest { Name = name }, cancellationToken: cancellationToken);
            return Success(ToConfigResponse(reply.Config));
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return NotFoundResponse();
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.InvalidArgument)
        {
            return ValidationError("name", Resources.Localization.API.invalid_value);
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpPut("{node}/vm/{name}/config")]
    [ProducesResponseType<Response<UpdateVirtualMachineConfigResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateVirtualMachineConfig(string node, string name, Control.Areas.Admin.Models.Requests.UpdateVirtualMachineConfigRequest request, CancellationToken cancellationToken)
    {
        if (request.VirtualCpuCount is null && request.MemoryMiB is null)
        {
            return ValidationError("virtualMachine", Resources.Localization.API.invalid_value);
        }
        if (request.VirtualCpuCount is 0)
        {
            return ValidationError("virtualCpuCount", Resources.Localization.API.invalid_value);
        }
        if (request.MemoryMiB is < 512)
        {
            return ValidationError("memoryMiB", Resources.Localization.API.invalid_value);
        }
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        // 配置更新体量小，且代理端有每虚拟机锁与创建/删除/扩容串行化，故同步执行而不走操作队列。
        Kvm.Contracts.UpdateVirtualMachineConfigRequest clusterRequest = new()
        {
            Name = name,
            ApplyLive = request.ApplyLive
        };
        if (request.VirtualCpuCount is { } virtualCpuCount)
        {
            clusterRequest.VirtualCpuCount = virtualCpuCount;
        }
        if (request.MemoryMiB is { } memoryMiB)
        {
            clusterRequest.MemoryMib = memoryMiB;
        }

        try
        {
            UpdateVirtualMachineConfigReply reply = await client.UpdateVirtualMachineConfigAsync(clusterRequest, cancellationToken: cancellationToken);
            return Success(new UpdateVirtualMachineConfigResponse(
                reply.PersistentApplied,
                reply.LiveApplied,
                reply.LiveMessage,
                reply.PersistentVirtualCpuCount,
                reply.PersistentMemoryMib,
                reply.LiveVirtualCpuCount,
                reply.LiveMemoryMib));
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return NotFoundResponse();
        }
        catch (RpcException exception) when (exception.StatusCode is Grpc.Core.StatusCode.InvalidArgument or Grpc.Core.StatusCode.FailedPrecondition)
        {
            return ValidationError("virtualMachine", Resources.Localization.API.invalid_value);
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpPost("{node}/vm/{name}/disk/resize")]
    [ProducesResponseType<Response<VirtualMachineSystemDiskResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResizeVirtualMachineDisk(string node, string name, Control.Areas.Admin.Models.Requests.ResizeVirtualMachineDiskRequest request, CancellationToken cancellationToken)
    {
        if (request.SizeGiB < 1)
        {
            return ValidationError("sizeGiB", Resources.Localization.API.invalid_value);
        }
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            ResizeVirtualMachineDiskReply reply = await client.ResizeVirtualMachineDiskAsync(
                new Kvm.Contracts.ResizeVirtualMachineDiskRequest { Name = name, SizeGib = request.SizeGiB },
                cancellationToken: cancellationToken);
            return Success(new VirtualMachineSystemDiskResponse(reply.SystemDisk.File, reply.SystemDisk.SizeGib));
        }
        catch (RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return NotFoundResponse();
        }
        catch (RpcException exception) when (exception.StatusCode is Grpc.Core.StatusCode.InvalidArgument or Grpc.Core.StatusCode.FailedPrecondition)
        {
            return ValidationError("sizeGiB", Resources.Localization.API.invalid_value);
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
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
            VncTicketCreation ticket = await vncConsoleTicketStore.CreateAsync(node, name, reply.SessionId, reply.Password, cancellationToken);
            Response.Cookies.Append("kvm-console-ticket", ticket.Token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = ticket.ExpiresAt,
                Path = "/api/v1/consoles"
            });
            string fragment = $"password={Uri.EscapeDataString(ticket.Password)}";
            return Success(new VncConsoleResponse($"/noVNC/kvm-console.html#{fragment}", ticket.ExpiresAt), StatusCodes.Status201Created);
        }
        catch (RpcException exception) when (exception.StatusCode is Grpc.Core.StatusCode.InvalidArgument or Grpc.Core.StatusCode.FailedPrecondition)
        {
            return ValidationError("console", Resources.Localization.API.invalid_value);
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

    private static ClusterHostMetricsResponse? ToHostMetricsResponse(NodeMetricsEntry? entry)
    {
        if (entry?.Snapshot?.Host is not { } host)
        {
            return null;
        }
        return new ClusterHostMetricsResponse(
            host.HasCpuUsageRatio ? host.CpuUsageRatio : null,
            host.HasLogicalCpuCount ? host.LogicalCpuCount : null,
            host.HasMemoryTotalBytes ? host.MemoryTotalBytes : null,
            host.HasMemoryAvailableBytes ? host.MemoryAvailableBytes : null,
            host.VirtualMachineDirectory,
            host.HasDiskTotalBytes ? host.DiskTotalBytes : null,
            host.HasDiskAvailableBytes ? host.DiskAvailableBytes : null,
            host.HasUptimeSeconds ? host.UptimeSeconds : null);
    }

    private static ClusterVirtualMachineMetricsResponse? ToVirtualMachineMetricsResponse(NodeMetricsEntry? entry, string name)
    {
        if (entry?.Snapshot is not { } snapshot)
        {
            return null;
        }
        VirtualMachineMetrics? metrics = snapshot.VirtualMachines
            .FirstOrDefault(virtualMachine => string.Equals(virtualMachine.Name, name, StringComparison.OrdinalIgnoreCase));
        return metrics is null ? null : new ClusterVirtualMachineMetricsResponse(
            metrics.HasCpuUsageRatio ? metrics.CpuUsageRatio : null,
            metrics.HasMemoryUsedBytes ? metrics.MemoryUsedBytes : null,
            metrics.HasAllocatedVirtualCpuCount ? metrics.AllocatedVirtualCpuCount : null,
            metrics.HasAllocatedMemoryBytes ? metrics.AllocatedMemoryBytes : null);
    }

    private static ClusterVirtualMachineSummaryResponse ToVirtualMachineSummary(NodeMetricsEntry? entry)
    {
        IReadOnlyList<VirtualMachineMetrics> virtualMachines = entry?.Snapshot is { } snapshot
            ? snapshot.VirtualMachines
            : [];
        return ClusterMetricsAggregator.SummarizeVirtualMachines(virtualMachines);
    }

    private static VirtualMachineResponse ToResponse(VirtualMachine source) => new(
        source.Name,
        source.Uuid,
        source.Id,
        ToResponseState(source.State),
        source.MemoryMib,
        source.VirtualCpuCount,
        source.Persistent);

    private static VirtualMachineConfigResponse ToConfigResponse(VirtualMachineConfig source) => new(
        source.Name,
        ToResponseState(source.State),
        source.Running,
        source.PersistentVirtualCpuCount,
        source.PersistentMemoryMib,
        source.LiveVirtualCpuCount,
        source.LiveMemoryMib,
        new VirtualMachineSystemDiskResponse(source.SystemDisk.File, source.SystemDisk.SizeGib));

    private static Model.Response.VirtualMachineState ToResponseState(Kvm.Contracts.VirtualMachineState state) => state switch
    {
        Kvm.Contracts.VirtualMachineState.Running => Model.Response.VirtualMachineState.Running,
        Kvm.Contracts.VirtualMachineState.Blocked => Model.Response.VirtualMachineState.Blocked,
        Kvm.Contracts.VirtualMachineState.Paused => Model.Response.VirtualMachineState.Paused,
        Kvm.Contracts.VirtualMachineState.Shutdown => Model.Response.VirtualMachineState.Shutdown,
        Kvm.Contracts.VirtualMachineState.Shutoff => Model.Response.VirtualMachineState.Shutoff,
        Kvm.Contracts.VirtualMachineState.Crashed => Model.Response.VirtualMachineState.Crashed,
        Kvm.Contracts.VirtualMachineState.Pmsuspended => Model.Response.VirtualMachineState.PowerManagementSuspended,
        _ => Model.Response.VirtualMachineState.Unspecified
    };
}
