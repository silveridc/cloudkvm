namespace Control.Services;

public sealed record OperationReservation(OperationEnqueueResult Result, Model.ControlOperation? Operation);
