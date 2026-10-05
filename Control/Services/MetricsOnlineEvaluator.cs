namespace Control.Services;

/// <summary>online/offline 判定：最后一次成功采样的时间距今不超过 N 个轮询周期即视为在线。</summary>
public static class MetricsOnlineEvaluator
{
    public static bool IsOnline(DateTimeOffset? lastSeenUtc, DateTimeOffset nowUtc, TimeSpan pollInterval, int offlineAfterMissedPolls)
    {
        if (lastSeenUtc is not { } lastSeen || pollInterval <= TimeSpan.Zero || offlineAfterMissedPolls < 1)
        {
            return false;
        }
        return nowUtc - lastSeen <= TimeSpan.FromTicks(pollInterval.Ticks * offlineAfterMissedPolls);
    }
}
