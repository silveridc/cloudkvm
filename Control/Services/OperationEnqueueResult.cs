namespace Control.Services;

public enum OperationEnqueueResult
{
    Created,
    Existing,
    Conflict,
    QueueFull
}
