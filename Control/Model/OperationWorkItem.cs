namespace Control.Model;

/// <summary>操作队列的工作项：操作对象与其执行体。</summary>
public sealed record OperationWorkItem(ControlOperation Operation, Func<CancellationToken, Task<object?>> Work);
