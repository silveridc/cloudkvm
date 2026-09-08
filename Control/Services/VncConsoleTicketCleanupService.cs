namespace Control.Services;

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
