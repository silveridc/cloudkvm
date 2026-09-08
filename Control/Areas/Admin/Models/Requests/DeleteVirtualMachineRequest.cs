namespace Control.Areas.Admin.Models.Requests;

public sealed record DeleteVirtualMachineRequest(bool Force, bool DeleteStorage = true);
