using Cluster.Interface;
namespace Cluster.Services;

/// <summary>周期清理过期 VNC 会话的后台服务。</summary>
public sealed class VncConsoleCleanupService(IVncConsoleService vncConsoleService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await vncConsoleService.CleanupExpiredSessionsAsync(stoppingToken);
        }
    }
}
