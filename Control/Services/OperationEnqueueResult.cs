namespace Control.Services;

/// <summary>操作入队结果。</summary>
public enum OperationEnqueueResult
{
    Created,
    Existing,
    Conflict,
    QueueFull
}
