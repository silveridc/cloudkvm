namespace Control.Model;

public sealed record OperationWorkItem(ControlOperation Operation, Func<CancellationToken, Task<object?>> Work);
