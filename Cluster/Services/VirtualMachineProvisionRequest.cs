namespace Cluster.Services;

/// <summary>虚拟机创建请求：名称、资源规格、基础镜像、网络与 cloud-init 配置。</summary>
public sealed record VirtualMachineProvisionRequest(
    string Name,
    ulong MemoryMiB,
    uint VirtualCpuCount,
    string BaseImage,
    string BridgeName,
    string MacAddress,
    CloudInitConfiguration CloudInit,
    bool Start);
