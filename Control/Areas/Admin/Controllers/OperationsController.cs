using Control.Model;
using Control.Model.Response;
using Control.Services;

namespace Control.Areas.Admin.Controllers;

[Area("Admin")]
[ApiController]
[Route("/admin/v1/clusters/{node}/operations")]
public sealed class OperationsController(OperationQueue operationQueue) : AdminControllerBase
{
    [HttpGet("{id}")]
    [ProducesResponseType<Response<OperationResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string node, string id, [FromQuery] int waitSeconds = 0, CancellationToken cancellationToken = default)
    {
        if (waitSeconds is < 0 or > 30)
        {
            return ValidationError("waitSeconds", Resources.Localization.API.invalid_wait_seconds);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        ControlOperation? operation;
        do
        {
            operation = await operationQueue.GetAsync(id, cancellationToken);
            if (operation is null || !string.Equals(operation.Node, node, StringComparison.OrdinalIgnoreCase))
            {
                return NotFoundResponse();
            }
            if (operation.Status is OperationStatus.Queued or OperationStatus.Running && !await operationQueue.IsOwnerAliveAsync(operation.OwnerId))
            {
                operation.Status = OperationStatus.Aborted;
                operation.GrpcStatusCode = Grpc.Core.StatusCode.Aborted.ToString();
                operation.Error = Resources.Localization.API.operation_owner_lost;
                operation.CompletedAt = DateTimeOffset.UtcNow;
                await operationQueue.SaveAsync(operation, CancellationToken.None);
            }
            if (operation.Status is not (OperationStatus.Queued or OperationStatus.Running) || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (true);

        return Success(ToResponse(operation));
    }

    private static OperationResponse ToResponse(ControlOperation operation) => new(
        operation.Id,
        operation.Type,
        operation.Node,
        operation.Status,
        operation.Result,
        operation.GrpcStatusCode,
        operation.Error,
        operation.CreatedAt,
        operation.CompletedAt);
}
