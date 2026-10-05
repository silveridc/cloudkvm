using Cluster.Interface;
using Kvm.Contracts;
using Microsoft.Extensions.Options;

namespace Cluster.Services;

/// <summary>
/// 每次调用采集一帧节点指标。宿主 CPU 用两次 /proc/stat 采样（间隔 200ms）差分得出；
/// 虚拟机 CPU 依赖累计的 cpu.time 计数器，上一帧采样保存在内存中跨两次调用差分，
/// 首次调用或计数器回退时该字段留空，不伪造数值。
/// </summary>
public sealed class NodeMetricsCollector(
    IVirshClient virshClient,
    IOptions<Models.Options.ProvisioningOptions> provisioningOptions,
    ILogger<NodeMetricsCollector> logger) : INodeMetricsCollector
{
    private const int HostCpuSamplingMilliseconds = 200;

    private sealed record VirtualMachineCpuSample(ulong CpuTimeNanoseconds, DateTimeOffset SampledAtUtc);

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, VirtualMachineCpuSample> _previousCpuSamples = new(StringComparer.OrdinalIgnoreCase);
    private uint _hostLogicalCpuCount = (uint)Math.Max(1, Environment.ProcessorCount);

    public async Task<NodeMetricsReply> GetNodeMetricsAsync(CancellationToken cancellationToken)
    {
        HostMetrics host = await CollectHostMetricsAsync(cancellationToken);
        List<VirtualMachineMetrics> virtualMachines = await CollectVirtualMachineMetricsAsync(cancellationToken);
        NodeMetricsReply reply = new() { Host = host };
        reply.VirtualMachines.AddRange(virtualMachines);
        return reply;
    }

    private async Task<HostMetrics> CollectHostMetricsAsync(CancellationToken cancellationToken)
    {
        HostMetrics metrics = new()
        {
            VirtualMachineDirectory = provisioningOptions.Value.VirtualMachineDirectory
        };

        if (!OperatingSystem.IsLinux())
        {
            return metrics;
        }

        try
        {
            ProcStatSample before = NodeMetricsParser.ParseProcStat(await File.ReadAllTextAsync("/proc/stat", cancellationToken));
            await Task.Delay(HostCpuSamplingMilliseconds, cancellationToken);
            ProcStatSample after = NodeMetricsParser.ParseProcStat(await File.ReadAllTextAsync("/proc/stat", cancellationToken));

            if (NodeMetricsParser.ComputeCpuUsageRatio(before, after) is { } cpuUsageRatio)
            {
                metrics.CpuUsageRatio = cpuUsageRatio;
            }
            if (after.LogicalProcessorCount > 0)
            {
                metrics.LogicalCpuCount = (uint)after.LogicalProcessorCount;
            }

            ProcMemInfo memory = NodeMetricsParser.ParseMemInfo(await File.ReadAllTextAsync("/proc/meminfo", cancellationToken));
            if (memory.TotalBytes is { } totalBytes)
            {
                metrics.MemoryTotalBytes = totalBytes;
            }
            if (memory.AvailableBytes is { } availableBytes)
            {
                metrics.MemoryAvailableBytes = availableBytes;
            }

            ProcUptime uptime = NodeMetricsParser.ParseUptime(await File.ReadAllTextAsync("/proc/uptime", cancellationToken));
            if (uptime.UptimeSeconds is { } uptimeSeconds)
            {
                metrics.UptimeSeconds = uptimeSeconds;
            }

            _hostLogicalCpuCount = metrics.HasLogicalCpuCount
                ? metrics.LogicalCpuCount
                : (uint)Math.Max(1, Environment.ProcessorCount);
            if (GetDirectoryDiskBytes(metrics.VirtualMachineDirectory) is { } disk)
            {
                metrics.DiskTotalBytes = disk.TotalBytes;
                metrics.DiskAvailableBytes = disk.AvailableBytes;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to collect host metrics from procfs.");
        }
        return metrics;
    }

    private async Task<List<VirtualMachineMetrics>> CollectVirtualMachineMetricsAsync(CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await virshClient.GetDomainStatsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to query virtual machine statistics from libvirt.");
            return [];
        }

        List<VirshDomainStats> domains = NodeMetricsParser.ParseDomainStats(output);
        DateTimeOffset sampledAtUtc = DateTimeOffset.UtcNow;
        List<VirtualMachineMetrics> virtualMachines = [];
        Dictionary<string, VirtualMachineCpuSample> nextSamples = new(StringComparer.OrdinalIgnoreCase);

        foreach (VirshDomainStats domain in domains)
        {
            bool managed;
            try
            {
                managed = await virshClient.IsManagedAsync(domain.Name, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to verify whether virtual machine {Name} is managed; it is excluded from metrics.", domain.Name);
                continue;
            }
            if (!managed)
            {
                continue;
            }

            VirtualMachineMetrics metrics = new() { Name = domain.Name };
            if (domain.BalloonCurrentKiB is { } balloonCurrentKiB)
            {
                metrics.MemoryUsedBytes = balloonCurrentKiB * 1024;
            }
            if (domain.VirtualCpuMaximum is { } virtualCpuMaximum)
            {
                metrics.AllocatedVirtualCpuCount = virtualCpuMaximum;
            }
            if (domain.BalloonMaximumKiB is { } balloonMaximumKiB)
            {
                metrics.AllocatedMemoryBytes = balloonMaximumKiB * 1024;
            }

            if (domain.CpuTimeNanoseconds is { } cpuTimeNanoseconds)
            {
                VirtualMachineCpuSample sample = new(cpuTimeNanoseconds, sampledAtUtc);
                nextSamples[domain.Name] = sample;
                VirtualMachineCpuSample? previous;
                lock (_syncRoot)
                {
                    _previousCpuSamples.TryGetValue(domain.Name, out previous);
                }
                if (previous is not null
                    && NodeMetricsParser.ComputeCpuTimeRatio(previous.CpuTimeNanoseconds, cpuTimeNanoseconds, sampledAtUtc - previous.SampledAtUtc, _hostLogicalCpuCount) is { } ratio)
                {
                    metrics.CpuUsageRatio = ratio;
                }
            }

            virtualMachines.Add(metrics);
        }

        lock (_syncRoot)
        {
            foreach (KeyValuePair<string, VirtualMachineCpuSample> sample in nextSamples)
            {
                _previousCpuSamples[sample.Key] = sample.Value;
            }
        }
        return virtualMachines;
    }

    private static (ulong TotalBytes, ulong AvailableBytes)? GetDirectoryDiskBytes(string directory)
    {
        try
        {
            List<string> driveNames = DriveInfo.GetDrives().Select(drive => drive.Name).ToList();
            string? driveName = NodeMetricsParser.SelectDriveNameForDirectory(directory, driveNames);
            if (driveName is null)
            {
                return null;
            }
            DriveInfo drive = new(driveName);
            if (!drive.IsReady || drive.TotalSize == 0)
            {
                return null;
            }
            return ((ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
