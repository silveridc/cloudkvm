namespace Control.Services;

public sealed class ControlInstanceHeartbeatService(OperationCache operationCache, ILogger<ControlInstanceHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await operationCache.RefreshOwnerAsync();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to refresh Control instance heartbeat.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
