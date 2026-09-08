using Control;
using Control.Model.Response;
using Control.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using ApiResponse = Control.Model.Response.Response;

namespace Control.Areas.Admin.Controllers;

public abstract class AdminControllerBase : ControllerBase
{
    protected bool TryGetClusterClient(IClusterClientFactory clusterClientFactory, string node, out Kvm.Contracts.ClusterAgent.ClusterAgentClient client, out JsonResult? error)
    {
        if (clusterClientFactory.TryCreate(node, out Kvm.Contracts.ClusterAgent.ClusterAgentClient? resolvedClient) && resolvedClient is not null)
        {
            client = resolvedClient;
            error = null;
            return true;
        }

        client = null!;
        error = NotFoundResponse();
        return false;
    }

    protected async Task<IActionResult> EnqueueOperationAsync<T>(OperationQueue operationQueue, string type, string node, T request, Func<CancellationToken, Task<object?>> work, CancellationToken cancellationToken)
    {
        if (!TryGetIdempotencyKey(out string idempotencyKey, out JsonResult? error))
        {
            return error!;
        }

        string json = JsonSerializer.Serialize(request);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        OperationReservation reservation = await operationQueue.EnqueueAsync(idempotencyKey, fingerprint, type, node, work, cancellationToken);
        return reservation.Result switch
        {
            OperationEnqueueResult.Created or OperationEnqueueResult.Existing when reservation.Operation is not null => AcceptedOperation(reservation.Operation),
            OperationEnqueueResult.Conflict => ApiResponse.Fail(Resources.Localization.API.idempotency_conflict, ResponseCode.Conflict, StatusCodes.Status409Conflict),
            _ => ApiResponse.Fail(Resources.Localization.API.operation_queue_full, ResponseCode.InternalError, StatusCodes.Status503ServiceUnavailable)
        };
    }

    protected bool TryGetIdempotencyKey(out string idempotencyKey, out JsonResult? error)
    {
        idempotencyKey = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrEmpty(idempotencyKey) || idempotencyKey.Length > 128)
        {
            error = ValidationError("Idempotency-Key", Resources.Localization.API.invalid_idempotency_key);
            return false;
        }
        error = null;
        return true;
    }

    protected JsonResult AcceptedOperation(Control.Model.ControlOperation operation)
    {
        Response.Headers.Location = $"/admin/v1/clusters/{Uri.EscapeDataString(operation.Node)}/operations/{operation.Id}";
        return ApiResponse.Success(Resources.Localization.API.operation_accepted, new OperationResponse(
            operation.Id,
            operation.Type,
            operation.Node,
            operation.Status,
            operation.Result,
            operation.GrpcStatusCode,
            operation.Error,
            operation.CreatedAt,
            operation.CompletedAt), StatusCodes.Status202Accepted);
    }

    protected static JsonResult Success<T>(T data, int httpStatusCode = StatusCodes.Status200OK)
    {
        return ApiResponse.Success(Resources.Localization.API.success_message, data, httpStatusCode);
    }

    protected static JsonResult Success(int httpStatusCode = StatusCodes.Status200OK)
    {
        return ApiResponse.Success(Resources.Localization.API.success_message, httpStatusCode);
    }

    protected static JsonResult NotFoundResponse()
    {
        return ApiResponse.Fail(Resources.Localization.API.resource_not_found, ResponseCode.NotFound, StatusCodes.Status404NotFound);
    }

    protected static JsonResult ValidationError(string field, string message)
    {
        return ApiResponse.Fail(Resources.Localization.API.request_error, new ValidationErrorResponse(new Dictionary<string, string[]> { [field] = [message] }), ResponseCode.RequestError, StatusCodes.Status400BadRequest);
    }

    protected static JsonResult ClusterError(RpcException exception)
    {
        return ApiResponse.Fail(Resources.Localization.API.cluster_request_failed, new GrpcErrorResponse(exception.StatusCode.ToString(), exception.Status.Detail), ResponseCode.InternalError, StatusCodes.Status502BadGateway);
    }
}
