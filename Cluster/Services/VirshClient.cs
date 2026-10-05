using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Cluster.Interface;

namespace Cluster.Services;

/// <summary>基于 virsh/qemu-img 命令行的 libvirt 客户端；变更类操作仅作用于受管 VM。</summary>
public sealed class VirshClient(IOptions<Models.Options.LibvirtOptions> options) : IVirshClient
{
    private const int CommandTimeoutSeconds = 30;
    private readonly string _uri = options.Value.Uri;

    public async Task<VirshHostStatus> GetHostStatusAsync(CancellationToken cancellationToken)
    {
        string uri = await RunAsync(["uri"], cancellationToken);
        string hostName = await RunAsync(["hostname"], cancellationToken);
        string version = await RunAsync(["version", "--daemon"], cancellationToken);

        return new VirshHostStatus(hostName, uri, "QEMU", version);
    }

    public async Task<IReadOnlyList<VirshVirtualMachine>> ListVirtualMachinesAsync(CancellationToken cancellationToken)
    {
        string output = await RunAsync(["list", "--all", "--name"], cancellationToken);
        string[] names = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        List<VirshVirtualMachine> virtualMachines = new(names.Length);

        foreach (string name in names)
        {
            if (await IsManagedAsync(name, cancellationToken))
            {
                virtualMachines.Add(await GetVirtualMachineRequiredAsync(name, cancellationToken));
            }
        }

        return virtualMachines;
    }

    public async Task<string> GetDomainStatsAsync(CancellationToken cancellationToken)
    {
        // 一次调用即返回所有活动域的 CPU、vCPU 与内存气球统计，无需逐台重复采样。
        return await RunAsync(["domstats", "--active", "--cpu-total", "--vcpu", "--balloon"], cancellationToken);
    }

    public async Task<VirshVirtualMachine?> GetVirtualMachineAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            VirshVirtualMachine virtualMachine = await GetVirtualMachineRequiredAsync(name, cancellationToken);
            return await IsManagedAsync(name, cancellationToken) ? virtualMachine : null;
        }
        catch (VirshCommandException exception) when (IsDomainNotFound(exception))
        {
            return null;
        }
    }

    public async Task<bool> DomainExistsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await GetVirtualMachineRequiredAsync(name, cancellationToken);
            return true;
        }
        catch (VirshCommandException exception) when (IsDomainNotFound(exception))
        {
            return false;
        }
    }

    public async Task<VirshVirtualMachine> ChangePowerStateAsync(string name, VirshPowerAction action, CancellationToken cancellationToken)
    {
        if (!await IsManagedAsync(name, cancellationToken))
        {
            throw new InvalidOperationException("Virtual machine is not managed by KvmControl.");
        }

        string command = action switch
        {
            VirshPowerAction.Start => "start",
            VirshPowerAction.Shutdown => "shutdown",
            VirshPowerAction.Reboot => "reboot",
            VirshPowerAction.ForceOff => "destroy",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        await RunAsync([command, name], cancellationToken);
        return await GetVirtualMachineRequiredAsync(name, cancellationToken);
    }

    public async Task DefineAsync(string domainXmlPath, CancellationToken cancellationToken)
    {
        await RunAsync(["define", domainXmlPath], cancellationToken);
    }

    public async Task UndefineAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(["undefine", name, "--nvram"], cancellationToken);
        }
        catch (VirshCommandException exception) when (IsDomainNotFound(exception))
        {
        }
    }

    public async Task<bool> UndefineIfUuidMatchesAsync(string name, string uuid, CancellationToken cancellationToken)
    {
        VirshVirtualMachine? virtualMachine = await GetVirtualMachineByNameAsync(name, cancellationToken);
        if (virtualMachine is null)
        {
            return true;
        }
        if (!string.Equals(virtualMachine.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (virtualMachine.State != VirshVirtualMachineState.Shutoff)
        {
            await ChangePowerStateAsync(name, VirshPowerAction.ForceOff, cancellationToken);
        }
        await UndefineAsync(name, cancellationToken);
        VirshVirtualMachine? remaining = await GetVirtualMachineByNameAsync(name, cancellationToken);
        return remaining is null;
    }

    public async Task<int> GetVncPortAsync(string name, CancellationToken cancellationToken)
    {
        string output = await RunAsync(["vncdisplay", name], cancellationToken);
        if (!output.StartsWith(':') || !int.TryParse(output[1..], out int display))
        {
            throw new InvalidOperationException("The virtual machine does not have a TCP VNC display.");
        }
        return 5900 + display;
    }

    public async Task<bool> IsManagedAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            string xml = await RunAsync(["dumpxml", name], cancellationToken);
            XDocument document = XDocument.Parse(xml, LoadOptions.None);
            XNamespace managedNamespace = "urn:kvmcontrol:managed";
            string? domainUuid = document.Root?.Element("uuid")?.Value.Trim();
            string? managedUuid = document.Root?.Element("metadata")?.Element(managedNamespace + "managed")?.Value.Trim();
            return Guid.TryParse(domainUuid, out Guid parsedDomainUuid)
                && Guid.TryParse(managedUuid, out Guid parsedManagedUuid)
                && parsedDomainUuid == parsedManagedUuid;
        }
        catch (VirshCommandException exception) when (IsDomainNotFound(exception))
        {
            return false;
        }
    }

    public async Task<VirshDomainXml> DumpXmlAsync(string name, bool inactive, CancellationToken cancellationToken)
    {
        string output = inactive
            ? await RunAsync(["dumpxml", "--inactive", name], cancellationToken)
            : await RunAsync(["dumpxml", name], cancellationToken);
        return new VirshDomainXml(output, VirshDomainXmlParser.Parse(output));
    }

    public async Task SetVirtualCpuCountLiveAsync(string name, uint virtualCpuCount, CancellationToken cancellationToken)
    {
        await RunAsync(["setvcpus", name, virtualCpuCount.ToString(CultureInfo.InvariantCulture), "--live"], cancellationToken);
    }

    public async Task SetMemoryLiveAsync(string name, ulong memoryKiB, CancellationToken cancellationToken)
    {
        await RunAsync(["setmem", name, memoryKiB.ToString(CultureInfo.InvariantCulture), "--live"], cancellationToken);
    }

    public async Task ResizeBlockDeviceAsync(string name, string targetDevice, ulong sizeBytes, CancellationToken cancellationToken)
    {
        await RunAsync(["blockresize", name, targetDevice, sizeBytes.ToString(CultureInfo.InvariantCulture)], cancellationToken);
    }

    public async Task ResizeDiskImageAsync(string diskPath, ulong sizeBytes, CancellationToken cancellationToken)
    {
        await RunQemuImgAsync(["resize", diskPath, sizeBytes.ToString(CultureInfo.InvariantCulture)], cancellationToken);
    }

    public async Task<ulong> GetDiskImageVirtualSizeBytesAsync(string diskPath, CancellationToken cancellationToken)
    {
        string output = await RunQemuImgAsync(["info", "--output=json", diskPath], cancellationToken);
        using JsonDocument document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("virtual-size", out JsonElement size) || size.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("The disk image information does not contain a virtual size.");
        }
        return size.GetUInt64();
    }

    public async Task SetVncPasswordAsync(string name, string password, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(password, "^[A-Za-z0-9+/]{8}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("VNC password format is invalid.", nameof(password));
        }
        await RunAsync(["qemu-monitor-command", name, "--hmp", $"set_password vnc {password}"], cancellationToken);
        await RunAsync(["qemu-monitor-command", name, "--hmp", $"expire_password vnc +{Math.Clamp((int)lifetime.TotalSeconds, 1, 1800)}"], cancellationToken);
    }

    public async Task ExpireVncPasswordAsync(string name, CancellationToken cancellationToken)
    {
        await RunAsync(["qemu-monitor-command", name, "--hmp", "expire_password vnc now"], cancellationToken);
    }

    private async Task<VirshVirtualMachine?> GetVirtualMachineByNameAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await GetVirtualMachineRequiredAsync(name, cancellationToken);
        }
        catch (VirshCommandException exception) when (IsDomainNotFound(exception))
        {
            return null;
        }
    }

    private async Task<VirshVirtualMachine> GetVirtualMachineRequiredAsync(string name, CancellationToken cancellationToken)
    {
        string output = await RunAsync(["dominfo", name], cancellationToken);
        Dictionary<string, string> values = ParseDomInfo(output);

        return new VirshVirtualMachine(
            values["Name"],
            values["UUID"],
            int.TryParse(values.GetValueOrDefault("Id"), out int id) ? id : -1,
            ParseState(values["State"]),
            ulong.TryParse(values.GetValueOrDefault("Max memory")?.Split(' ', 2)[0], out ulong memoryKiB) ? memoryKiB / 1024 : 0,
            uint.TryParse(values.GetValueOrDefault("CPU(s)"), out uint virtualCpuCount) ? virtualCpuCount : 0,
            string.Equals(values.GetValueOrDefault("Persistent"), "yes", StringComparison.OrdinalIgnoreCase),
            ulong.TryParse(values.GetValueOrDefault("Used memory")?.Split(' ', 2)[0], out ulong usedMemoryKiB) ? usedMemoryKiB / 1024 : 0);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        List<string> fullArguments = ["--connect", _uri, .. arguments];
        return await RunProcessAsync("virsh", fullArguments, cancellationToken);
    }

    private async Task<string> RunQemuImgAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        return await RunProcessAsync("qemu-img", arguments, cancellationToken);
    }

    private static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(CommandTimeoutSeconds));
        using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(linkedTokenSource.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(linkedTokenSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        string output = await standardOutput;
        string error = await standardError;

        if (process.ExitCode != 0)
        {
            throw new VirshCommandException(process.ExitCode, error.Trim());
        }

        return output.Trim();
    }

    private static void KillProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(true);
            process.WaitForExit();
        }
    }

    private static bool IsDomainNotFound(VirshCommandException exception)
    {
        return exception.StandardError.Contains("failed to get domain", StringComparison.OrdinalIgnoreCase)
            || exception.StandardError.Contains("domain not found", StringComparison.OrdinalIgnoreCase);
    }

    private static VirshVirtualMachineState ParseState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "running" => VirshVirtualMachineState.Running,
            "blocked" => VirshVirtualMachineState.Blocked,
            "paused" => VirshVirtualMachineState.Paused,
            "in shutdown" or "shutdown" => VirshVirtualMachineState.Shutdown,
            "shut off" or "shutoff" => VirshVirtualMachineState.Shutoff,
            "crashed" => VirshVirtualMachineState.Crashed,
            "pmsuspended" => VirshVirtualMachineState.PowerManagementSuspended,
            _ => VirshVirtualMachineState.Unspecified
        };
    }

    private static Dictionary<string, string> ParseDomInfo(string output)
    {
        return output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
    }
}
