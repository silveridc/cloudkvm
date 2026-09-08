using Control.Model.Response;
using Control.Services;
using Grpc.Core;
using Kvm.Contracts;
using HttpCreateBridgeRequest = Control.Areas.Admin.Models.Requests.CreateBridgeRequest;
using HttpCreateNatRuleRequest = Control.Areas.Admin.Models.Requests.CreateNatRuleRequest;

namespace Control.Areas.Admin.Controllers;

[Area("Admin")]
[ApiController]
[Route("/admin/v1/clusters/{node}/networks")]
public sealed class NetworksController(IClusterClientFactory clusterClientFactory, OperationQueue operationQueue) : AdminControllerBase
{
    [HttpGet("bridges")]
    [ProducesResponseType<Response<IReadOnlyList<BridgeResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBridges(string node, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            ListNetworksReply reply = await client.ListNetworksAsync(new ListNetworksRequest(), cancellationToken: cancellationToken);
            return Success(reply.Networks.Select(ToResponse).ToArray());
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpPost("bridges")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateBridge(string node, HttpCreateBridgeRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetBridgeType(request.Type, out BridgeType type))
        {
            return ValidationError("type", Resources.Localization.API.invalid_bridge_type);
        }
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        return await EnqueueOperationAsync(
            operationQueue,
            "network.bridge.create",
            node,
            request,
            async cancellationToken =>
            {
                NetworkReply reply = await client.CreateBridgeAsync(
                    new Kvm.Contracts.CreateBridgeRequest
                    {
                        Name = request.Name,
                        Type = type,
                        Ports = { request.Ports ?? [] }
                    },
                    cancellationToken: cancellationToken);
                return ToResponse(reply.Network);
            }, cancellationToken);
    }

    [HttpDelete("bridges/{name}")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> DeleteBridge(string node, string name, [FromQuery] string type, CancellationToken cancellationToken)
    {
        if (!TryGetBridgeType(type, out BridgeType bridgeType))
        {
            return ValidationError("type", Resources.Localization.API.invalid_bridge_type);
        }
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        return await EnqueueOperationAsync(
            operationQueue,
            "network.bridge.delete",
            node,
            new { name, type },
            async cancellationToken =>
            {
                await client.DeleteBridgeAsync(new DeleteBridgeRequest { Name = name, Type = bridgeType }, cancellationToken: cancellationToken);
                return null;
            }, cancellationToken);
    }

    [HttpGet("nat-rules")]
    [ProducesResponseType<Response<IReadOnlyList<NatRuleResponse>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListNatRules(string node, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        try
        {
            ListNatRulesReply reply = await client.ListNatRulesAsync(new ListNatRulesRequest(), cancellationToken: cancellationToken);
            return Success(reply.Rules.Select(ToResponse).ToArray());
        }
        catch (RpcException exception)
        {
            return ClusterError(exception);
        }
    }

    [HttpPost("nat-rules")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateNatRule(string node, HttpCreateNatRuleRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        return await EnqueueOperationAsync(
            operationQueue,
            "network.nat.create",
            node,
            request,
            async cancellationToken =>
            {
                NatRuleReply reply = await client.CreateNatRuleAsync(
                    new Kvm.Contracts.CreateNatRuleRequest
                    {
                        Protocol = request.Protocol,
                        ListenAddress = request.ListenAddress,
                        ListenPort = request.ListenPort,
                        TargetAddress = request.TargetAddress,
                        TargetPort = request.TargetPort
                    },
                    cancellationToken: cancellationToken);
                return ToResponse(reply.Rule);
            }, cancellationToken);
    }

    [HttpDelete("nat-rules/{id}")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> DeleteNatRule(string node, string id, CancellationToken cancellationToken)
    {
        if (!TryGetClusterClient(clusterClientFactory, node, out ClusterAgent.ClusterAgentClient client, out JsonResult? error))
        {
            return error!;
        }

        return await EnqueueOperationAsync(
            operationQueue,
            "network.nat.delete",
            node,
            new { id },
            async cancellationToken =>
            {
                await client.DeleteNatRuleAsync(new DeleteNatRuleRequest { Id = id }, cancellationToken: cancellationToken);
                return null;
            }, cancellationToken);
    }

    private static bool TryGetBridgeType(string type, out BridgeType bridgeType)
    {
        bridgeType = type.ToLowerInvariant() switch
        {
            "linux" => BridgeType.Linux,
            "open-vswitch" => BridgeType.OpenVswitch,
            _ => BridgeType.Unspecified
        };
        return bridgeType != BridgeType.Unspecified;
    }

    private static BridgeResponse ToResponse(Network source) => new(source.Name, source.Type == BridgeType.Linux ? "linux" : "open-vswitch", source.Ports, source.Up);

    private static NatRuleResponse ToResponse(NatRule source) => new(source.Id, source.Protocol, source.ListenAddress, source.ListenPort, source.TargetAddress, source.TargetPort);
}
