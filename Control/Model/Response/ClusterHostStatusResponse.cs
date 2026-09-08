namespace Control.Model.Response;

public sealed record ClusterHostStatusResponse(
    string Node,
    string HostName,
    string LibvirtUri,
    string HypervisorType,
    string HypervisorVersion);
