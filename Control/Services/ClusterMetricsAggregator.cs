using Control.Model.Response;
using Kvm.Contracts;

namespace Control.Services;

/// <summary>把 store 中的各 VM 指标汇总为集群列表展示的总量；缺字段的条目不参与对应合计。</summary>
public static class ClusterMetricsAggregator
{
    public static ClusterVirtualMachineSummaryResponse SummarizeVirtualMachines(IReadOnlyList<VirtualMachineMetrics> virtualMachines)
    {
        ulong? allocatedVirtualCpuCount = null;
        ulong? allocatedMemoryMiB = null;
        ulong virtualCpuSum = 0;
        ulong memoryBytesSum = 0;
        bool anyVirtualCpu = false;
        bool anyMemory = false;

        foreach (VirtualMachineMetrics virtualMachine in virtualMachines)
        {
            if (virtualMachine.HasAllocatedVirtualCpuCount)
            {
                virtualCpuSum += virtualMachine.AllocatedVirtualCpuCount;
                anyVirtualCpu = true;
            }
            if (virtualMachine.HasAllocatedMemoryBytes)
            {
                memoryBytesSum += virtualMachine.AllocatedMemoryBytes;
                anyMemory = true;
            }
        }

        if (anyVirtualCpu)
        {
            allocatedVirtualCpuCount = virtualCpuSum;
        }
        if (anyMemory)
        {
            allocatedMemoryMiB = memoryBytesSum / (1024UL * 1024UL);
        }
        return new ClusterVirtualMachineSummaryResponse(virtualMachines.Count, allocatedVirtualCpuCount, allocatedMemoryMiB);
    }
}
