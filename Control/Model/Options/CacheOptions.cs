namespace Control.Model.Options;

/// <summary>缓存与保留期配置（Cache 配置节）：实例名、操作保留时长与 VNC 票据有效期。</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public string InstanceName { get; set; } = "KvmControl";
    public int OperationRetentionHours { get; set; } = 24;
    public int VncTicketLifetimeMinutes { get; set; } = 5;
}
