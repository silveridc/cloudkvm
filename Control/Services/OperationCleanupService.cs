namespace Control.Services;

public sealed class OperationCleanupService(OperationQueue operationQueue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            operationQueue.RemoveExpired(TimeSpan.FromHours(24));
        }
    }
}
