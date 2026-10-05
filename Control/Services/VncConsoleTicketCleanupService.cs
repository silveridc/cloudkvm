namespace Control.Services;

/// <summary>周期清理过期 VNC 票据的后台服务。</summary>
public sealed class VncConsoleTicketCleanupService(VncConsoleTicketStore ticketStore) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ticketStore.RemoveExpiredAsync(stoppingToken);
        }
    }
}
