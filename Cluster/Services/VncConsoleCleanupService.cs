namespace Cluster.Services;

public sealed class VncConsoleCleanupService(IVncConsoleService vncConsoleService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await vncConsoleService.CleanupExpiredSessionsAsync();
        }
    }
}
