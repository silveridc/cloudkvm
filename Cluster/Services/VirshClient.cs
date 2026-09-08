using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cluster.Services;

public sealed class VirshClient(IOptions<Models.Options.LibvirtOptions> options) : IVirshClient
{
    private const int CommandTimeoutSeconds = 30;
    private readonly string uri = options.Value.Uri;

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
            virtualMachines.Add(await GetVirtualMachineRequiredAsync(name, cancellationToken));
        }

        return virtualMachines;
    }

    public async Task<VirshVirtualMachine?> GetVirtualMachineAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await GetVirtualMachineRequiredAsync(name, cancellationToken);
        }
        catch (VirshCommandException exception) when (exception.StandardError.Contains("failed to get domain", StringComparison.OrdinalIgnoreCase)
            || exception.StandardError.Contains("domain not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public async Task<VirshVirtualMachine> ChangePowerStateAsync(string name, VirshPowerAction action, CancellationToken cancellationToken)
    {
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
        catch (VirshCommandException exception) when (exception.StandardError.Contains("domain not found", StringComparison.OrdinalIgnoreCase))
        {
        }
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
        string xml = await RunAsync(["dumpxml", name], cancellationToken);
        return xml.Contains("<kvmcontrol:managed", StringComparison.Ordinal)
            && xml.Contains("xmlns:kvmcontrol='urn:kvmcontrol:managed'", StringComparison.Ordinal);
    }

    public async Task SetVncPasswordAsync(string name, string password, CancellationToken cancellationToken)
    {
        string command = $"set_password vnc {password}";
        await RunAsync(["qemu-monitor-command", name, "--hmp", command], cancellationToken);
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
            string.Equals(values.GetValueOrDefault("Persistent"), "yes", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("virsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--connect");
        startInfo.ArgumentList.Add(uri);

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start virsh.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(CommandTimeoutSeconds));
        using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(linkedTokenSource.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(linkedTokenSource.Token);
        await process.WaitForExitAsync(linkedTokenSource.Token);
        string output = await standardOutput;
        string error = await standardError;

        if (process.ExitCode != 0)
        {
            throw new VirshCommandException(process.ExitCode, error.Trim());
        }

        return output.Trim();
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
