namespace Control.Model.Response;

/// <summary>异步操作的状态。</summary>
public enum OperationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Aborted
}
