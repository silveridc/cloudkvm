namespace Control.Model.Options;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public string InstanceName { get; set; } = "KvmControl";
    public int OperationRetentionHours { get; set; } = 24;
    public int VncTicketLifetimeMinutes { get; set; } = 5;
}
