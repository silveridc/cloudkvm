namespace Control.Model.Options;

/// <summary>操作队列配置（Operations 配置节）：队列容量、工作线程数与执行超时。</summary>
public sealed class OperationOptions
{
    public const string SectionName = "Operations";

    public int QueueCapacity { get; set; } = 64;
    public int WorkerCount { get; set; } = 4;
    public int ExecutionTimeoutMinutes { get; set; } = 30;
}
