using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cluster.Services;

public sealed class HostNetworkClient : IHostNetworkClient
{
    private const string NatTable = "kvmcontrol";
    private const int CommandTimeoutSeconds = 30;
    private static readonly Regex InterfaceNamePattern = new("^[A-Za-z0-9_.-]{1,15}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NatCommentPattern = new("^kvmcontrol:(?<id>[a-f0-9]{32}):(?<protocol>tcp|udp):(?<listenAddress>[^:]+):(?<listenPort>\\d+):(?<targetAddress>[^:]+):(?<targetPort>\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<HostBridge>> ListBridgesAsync(CancellationToken cancellationToken)
    {
        List<HostBridge> bridges = await ListLinuxBridgesAsync(cancellationToken);
        bridges.AddRange(await ListOpenVSwitchBridgesAsync(cancellationToken));
        return bridges.OrderBy(bridge => bridge.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<HostBridge> CreateBridgeAsync(string name, HostBridgeType type, IReadOnlyList<string> ports, CancellationToken cancellationToken)
    {
        ValidateInterfaceName(name);
        foreach (string port in ports)
        {
            ValidateInterfaceName(port);
        }

        switch (type)
        {
            case HostBridgeType.Linux:
                await RunAsync("ip", ["link", "add", "name", name, "type", "bridge"], cancellationToken);
                try
                {
                    foreach (string port in ports)
                    {
                        await RunAsync("ip", ["link", "set", "dev", port, "master", name], cancellationToken);
                    }
                    await RunAsync("ip", ["link", "set", "dev", name, "up"], cancellationToken);
                }
                catch
                {
                    await RunAsync("ip", ["link", "delete", "dev", name, "type", "bridge"], CancellationToken.None);
                    throw;
                }
                break;
            case HostBridgeType.OpenVSwitch:
                await RunAsync("ovs-vsctl", ["add-br", name], cancellationToken);
                try
                {
                    foreach (string port in ports)
                    {
                        await RunAsync("ovs-vsctl", ["add-port", name, port], cancellationToken);
                    }
                    await RunAsync("ip", ["link", "set", "dev", name, "up"], cancellationToken);
                }
                catch
                {
                    await RunAsync("ovs-vsctl", ["del-br", name], CancellationToken.None);
                    throw;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        return new HostBridge(name, type, ports, true);
    }

    public Task DeleteBridgeAsync(string name, HostBridgeType type, CancellationToken cancellationToken)
    {
        ValidateInterfaceName(name);
        return type switch
        {
            HostBridgeType.Linux => RunAsync("ip", ["link", "delete", "dev", name, "type", "bridge"], cancellationToken),
            HostBridgeType.OpenVSwitch => RunAsync("ovs-vsctl", ["del-br", name], cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    public async Task<IReadOnlyList<HostNatRule>> ListNatRulesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NatRuleEntry> entries = await GetNatRuleEntriesAsync(cancellationToken);
        return entries
            .GroupBy(entry => entry.Rule.Id, StringComparer.Ordinal)
            .Select(group => group.First().Rule)
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<HostNatRule> CreateNatRuleAsync(string protocol, string listenAddress, ushort listenPort, string targetAddress, ushort targetPort, CancellationToken cancellationToken)
    {
        ValidateProtocol(protocol);
        ValidateIpv4Address(listenAddress, nameof(listenAddress));
        ValidateIpv4Address(targetAddress, nameof(targetAddress));
        string id = Guid.NewGuid().ToString("N");
        string comment = $"kvmcontrol:{id}:{protocol}:{listenAddress}:{listenPort}:{targetAddress}:{targetPort}";

        await EnsureNatTableAsync(cancellationToken);
        await RunAsync("nft", ["add", "rule", "ip", NatTable, "prerouting", "ip", "daddr", listenAddress, protocol, "dport", listenPort.ToString(), "dnat", "to", $"{targetAddress}:{targetPort}", "comment", comment], cancellationToken);
        try
        {
            await RunAsync("nft", ["add", "rule", "ip", NatTable, "forward", "ip", "daddr", targetAddress, protocol, "dport", targetPort.ToString(), "accept", "comment", comment], cancellationToken);
            await RunAsync("nft", ["add", "rule", "ip", NatTable, "postrouting", "ip", "daddr", targetAddress, protocol, "dport", targetPort.ToString(), "masquerade", "comment", comment], cancellationToken);
        }
        catch
        {
            await DeleteNatRuleAsync(id, CancellationToken.None);
            throw;
        }

        return new HostNatRule(id, protocol, listenAddress, listenPort, targetAddress, targetPort);
    }

    public async Task DeleteNatRuleAsync(string id, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("The NAT rule id is invalid.", nameof(id));
        }

        foreach (NatRuleEntry entry in await GetNatRuleEntriesAsync(cancellationToken))
        {
            if (entry.Rule.Id == id)
            {
                await RunAsync("nft", ["delete", "rule", "ip", NatTable, entry.Chain, "handle", entry.Handle.ToString()], cancellationToken);
            }
        }
    }

    private static async Task<List<HostBridge>> ListLinuxBridgesAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await TryRunAsync("ip", ["--json", "link", "show", "type", "bridge"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        List<HostBridge> bridges = [];
        foreach (JsonElement bridge in document.RootElement.EnumerateArray())
        {
            string name = bridge.GetProperty("ifname").GetString()!;
            CommandResult portsResult = await TryRunAsync("bridge", ["--json", "link", "show", "master", name], cancellationToken);
            string[] ports = portsResult.ExitCode == 0
                ? GetInterfaceNames(portsResult.StandardOutput)
                : [];
            bool up = string.Equals(bridge.GetProperty("operstate").GetString(), "UP", StringComparison.OrdinalIgnoreCase);
            bridges.Add(new HostBridge(name, HostBridgeType.Linux, ports, up));
        }

        return bridges;
    }

    private static async Task<List<HostBridge>> ListOpenVSwitchBridgesAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await TryRunAsync("ovs-vsctl", ["list-br"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        List<HostBridge> bridges = [];
        foreach (string name in result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            CommandResult portsResult = await TryRunAsync("ovs-vsctl", ["list-ports", name], cancellationToken);
            CommandResult linkResult = await TryRunAsync("ip", ["--json", "link", "show", "dev", name], cancellationToken);
            bool up = linkResult.ExitCode == 0 && IsLinkUp(linkResult.StandardOutput);
            bridges.Add(new HostBridge(name, HostBridgeType.OpenVSwitch, portsResult.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), up));
        }

        return bridges;
    }

    private async Task EnsureNatTableAsync(CancellationToken cancellationToken)
    {
        CommandResult table = await TryRunAsync("nft", ["list", "table", "ip", NatTable], cancellationToken);
        if (table.ExitCode == 0)
        {
            return;
        }

        await RunAsync("nft", ["add", "table", "ip", NatTable], cancellationToken);
        try
        {
            await RunAsync("nft", ["add", "chain", "ip", NatTable, "prerouting", "{", "type", "nat", "hook", "prerouting", "priority", "dstnat;", "policy", "accept;", "}"], cancellationToken);
            await RunAsync("nft", ["add", "chain", "ip", NatTable, "postrouting", "{", "type", "nat", "hook", "postrouting", "priority", "srcnat;", "policy", "accept;", "}"], cancellationToken);
            await RunAsync("nft", ["add", "chain", "ip", NatTable, "forward", "{", "type", "filter", "hook", "forward", "priority", "filter;", "policy", "accept;", "}"], cancellationToken);
        }
        catch
        {
            await RunAsync("nft", ["delete", "table", "ip", NatTable], CancellationToken.None);
            throw;
        }
    }

    private async Task<IReadOnlyList<NatRuleEntry>> GetNatRuleEntriesAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await TryRunAsync("nft", ["--json", "--handle", "list", "table", "ip", NatTable], cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        List<NatRuleEntry> entries = [];
        foreach (JsonElement item in document.RootElement.GetProperty("nftables").EnumerateArray())
        {
            if (!item.TryGetProperty("rule", out JsonElement rule) || !rule.TryGetProperty("comment", out JsonElement comment))
            {
                continue;
            }

            Match match = NatCommentPattern.Match(comment.GetString() ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            entries.Add(new NatRuleEntry(
                rule.GetProperty("chain").GetString()!,
                rule.GetProperty("handle").GetInt32(),
                new HostNatRule(
                    match.Groups["id"].Value,
                    match.Groups["protocol"].Value,
                    match.Groups["listenAddress"].Value,
                    ushort.Parse(match.Groups["listenPort"].Value),
                    match.Groups["targetAddress"].Value,
                    ushort.Parse(match.Groups["targetPort"].Value))));
        }

        return entries;
    }

    private static string[] GetInterfaceNames(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement
            .EnumerateArray()
            .Select(element => element.GetProperty("ifname").GetString())
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
    }

    private static bool IsLinkUp(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement
            .EnumerateArray()
            .Any(element => string.Equals(element.GetProperty("operstate").GetString(), "UP", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateInterfaceName(string name)
    {
        if (!InterfaceNamePattern.IsMatch(name))
        {
            throw new ArgumentException("Interface names must contain only letters, digits, dots, hyphens, or underscores and be at most 15 characters.", nameof(name));
        }
    }

    private static void ValidateProtocol(string protocol)
    {
        if (protocol is not ("tcp" or "udp"))
        {
            throw new ArgumentException("NAT protocol must be tcp or udp.", nameof(protocol));
        }
    }

    private static void ValidateIpv4Address(string address, string parameterName)
    {
        if (!IPAddress.TryParse(address, out IPAddress? parsedAddress) || parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("An IPv4 address is required.", parameterName);
        }
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        CommandResult result = await TryRunAsync(fileName, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new HostNetworkCommandException(fileName, result.ExitCode, result.StandardError.Trim());
        }
    }

    private static async Task<CommandResult> TryRunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
        await process.WaitForExitAsync(linkedTokenSource.Token);
        return new CommandResult(process.ExitCode, await standardOutput, await standardError);
    }

}
