using System.Globalization;

namespace Cluster.Services;

/// <summary>单台虚拟机的 domstats 采样结果。</summary>
public sealed record VirshDomainStats(
    string Name,
    ulong? CpuTimeNanoseconds,
    uint? VirtualCpuCurrent,
    uint? VirtualCpuMaximum,
    ulong? BalloonCurrentKiB,
    ulong? BalloonMaximumKiB,
    ulong? State);

/// <summary>/proc/meminfo 解析结果，单位字节。</summary>
public sealed record ProcMemInfo(ulong? TotalBytes, ulong? AvailableBytes);

/// <summary>/proc/uptime 解析结果。</summary>
public sealed record ProcUptime(ulong? UptimeSeconds);

/// <summary>/proc/stat 单次采样；idle 只计 idle 字段（iowait 在部分内核上不单调），iowait 归入 busy。</summary>
public sealed record ProcStatSample(ulong Idle, ulong Total, int LogicalProcessorCount);

/// <summary>/proc 文件与 virsh domstats 输出的纯解析函数，便于在任何平台做回归测试。</summary>
public static class NodeMetricsParser
{
    public static ProcStatSample ParseProcStat(string content)
    {
        ulong idle = 0;
        ulong total = 0;
        bool sawCpuLine = false;
        int logicalProcessorCount = 0;

        foreach (string line in content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2)
            {
                continue;
            }
            // 字段顺序：user nice system idle iowait irq softirq steal ...
            if (string.Equals(fields[0], "cpu", StringComparison.Ordinal))
            {
                sawCpuLine = true;
                ulong[] values = ParseUlongs(fields, 1);
                for (int index = 0; index < values.Length; index++)
                {
                    total += values[index];
                    if (index == 3)
                    {
                        idle += values[index];
                    }
                }
            }
            else if (fields[0].StartsWith("cpu", StringComparison.Ordinal) && uint.TryParse(fields[0].AsSpan("cpu".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                logicalProcessorCount++;
            }
        }

        return sawCpuLine ? new ProcStatSample(idle, total, logicalProcessorCount) : new ProcStatSample(0, 0, 0);
    }

    public static double? ComputeCpuUsageRatio(ProcStatSample before, ProcStatSample after)
    {
        if (after.Total <= before.Total)
        {
            return null;
        }
        if (after.Idle < before.Idle)
        {
            return null;
        }
        return 1.0 - (double)(after.Idle - before.Idle) / (after.Total - before.Total);
    }

    public static ProcMemInfo ParseMemInfo(string content)
    {
        ulong? totalKiB = null;
        ulong? availableKiB = null;
        ulong? freeKiB = null;

        foreach (string line in content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            string key = line[..separator].Trim();
            string valueText = line[(separator + 1)..].Trim();
            string unit = "kB";
            int unitSeparator = valueText.IndexOf(' ');
            if (unitSeparator >= 0)
            {
                unit = valueText[(unitSeparator + 1)..].Trim();
                valueText = valueText[..unitSeparator].Trim();
            }
            // 内核只保证 kB 单位，其他单位跳过以免误读。
            if (!string.Equals(unit, "kB", StringComparison.Ordinal)
                || !ulong.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong valueKiB))
            {
                continue;
            }
            if (string.Equals(key, "MemTotal", StringComparison.Ordinal))
            {
                totalKiB = valueKiB;
            }
            else if (string.Equals(key, "MemAvailable", StringComparison.Ordinal))
            {
                availableKiB = valueKiB;
            }
            else if (string.Equals(key, "MemFree", StringComparison.Ordinal))
            {
                freeKiB = valueKiB;
            }
        }

        // MemAvailable 含可回收缓存，老内核没有时退回 MemFree。
        ulong? availableBytes = (availableKiB ?? freeKiB) is { } kiB ? kiB * 1024 : null;
        return new ProcMemInfo(totalKiB is { } total ? total * 1024 : null, availableBytes);
    }

    public static ProcUptime ParseUptime(string content)
    {
        string firstToken = content.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return new ProcUptime(null);
        }
        return new ProcUptime((ulong)Math.Floor(seconds));
    }

    public static List<VirshDomainStats> ParseDomainStats(string output)
    {
        List<VirshDomainStats> domains = [];
        string? currentName = null;
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Domain: '", StringComparison.Ordinal) && line.EndsWith('\'') && line.Length > "Domain: ''".Length)
            {
                FlushDomain(domains, currentName, values);
                currentName = line["Domain: '".Length..^1];
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int separator = token.IndexOf('=');
                if (separator <= 0 || separator == token.Length - 1)
                {
                    continue;
                }
                values[token[..separator]] = token[(separator + 1)..];
            }
        }
        FlushDomain(domains, currentName, values);
        return domains;
    }

    private static void FlushDomain(List<VirshDomainStats> domains, string? name, Dictionary<string, string> values)
    {
        if (name is null || values.Count == 0)
        {
            return;
        }
        domains.Add(new VirshDomainStats(
            name,
            ParseUlong(values.GetValueOrDefault("cpu.time")),
            ParseUInt(values.GetValueOrDefault("vcpu.current")),
            ParseUInt(values.GetValueOrDefault("vcpu.maximum")),
            ParseUlong(values.GetValueOrDefault("balloon.current")),
            ParseUlong(values.GetValueOrDefault("balloon.maximum")),
            ParseUlong(values.GetValueOrDefault("state.state"))));
    }

    /// <summary>按两次快照的 cpu.time 差分计算 CPU 占用率；计时器回退或间隔无效时返回 null，比率上限 1。</summary>
    public static double? ComputeCpuTimeRatio(ulong previousCpuTimeNanoseconds, ulong currentCpuTimeNanoseconds, TimeSpan elapsed, uint logicalCpuCount)
    {
        if (elapsed <= TimeSpan.Zero || logicalCpuCount == 0 || currentCpuTimeNanoseconds < previousCpuTimeNanoseconds)
        {
            return null;
        }
        double elapsedNanoseconds = elapsed.TotalSeconds * 1_000_000_000.0 * logicalCpuCount;
        if (elapsedNanoseconds <= 0)
        {
            return null;
        }
        return Math.Min((currentCpuTimeNanoseconds - previousCpuTimeNanoseconds) / elapsedNanoseconds, 1.0);
    }

    /// <summary>按最长前缀匹配选出目录所在的挂载点；前缀必须落在路径边界上，避免 /var 误配 /varlib。</summary>
    public static string? SelectDriveNameForDirectory(string directory, IEnumerable<string> driveNames)
    {
        string normalizedDirectory = NormalizeForPrefixMatch(directory);
        string? best = null;
        int bestLength = -1;

        foreach (string driveName in driveNames)
        {
            string normalizedDrive = NormalizeForPrefixMatch(driveName);
            bool isRoot = normalizedDrive is "/" or "\\" || normalizedDrive.Length == 3 && normalizedDrive[1] == ':' && (normalizedDrive[2] == '\\' || normalizedDrive[2] == '/');
            bool matches = normalizedDirectory.StartsWith(normalizedDrive, StringComparison.OrdinalIgnoreCase)
                && (isRoot
                    || normalizedDirectory.Length == normalizedDrive.Length
                    || normalizedDirectory[normalizedDrive.Length] is '/' or '\\');
            if (matches && normalizedDrive.Length > bestLength)
            {
                best = driveName;
                bestLength = normalizedDrive.Length;
            }
        }
        return best;
    }

    private static string NormalizeForPrefixMatch(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 ? path[..1] : trimmed;
    }

    private static ulong? ParseUlong(string? value)
    {
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : null;
    }

    private static uint? ParseUInt(string? value)
    {
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) && parsed <= uint.MaxValue ? (uint)parsed : null;
    }

    private static ulong[] ParseUlongs(string[] fields, int start)
    {
        List<ulong> values = [];
        for (int index = start; index < fields.Length; index++)
        {
            if (ulong.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            {
                values.Add(value);
            }
        }
        return [.. values];
    }
}

