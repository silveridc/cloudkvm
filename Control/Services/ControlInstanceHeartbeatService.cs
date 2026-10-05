namespace Control.Services;

/// <summary>每 10 秒续期本实例存活标记的后台服务。</summary>
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
