namespace Control.Services;

/// <summary>幂等预留结果：入队结果与命中的操作。</summary>
public sealed record OperationReservation(OperationEnqueueResult Result, Model.ControlOperation? Operation);
