namespace Control.Areas.Admin.Models.Requests;

/// <summary>创建虚拟机请求：名称、资源规格、基础镜像、网络与 cloud-init 参数。</summary>
public sealed record CreateVirtualMachineRequest(
    string Name,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    string BaseImage,
    string BridgeName,
    string MacAddress,
    CloudInitRequest CloudInit,
    bool Start);
