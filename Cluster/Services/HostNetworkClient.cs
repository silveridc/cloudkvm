using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cluster.Interface;

namespace Cluster.Services;

public sealed class HostNetworkClient(IOptions<Models.Options.NetworkOptions> options) : IHostNetworkClient
{
    private const string NatTable = "kvmcontrol";
    private const int CommandTimeoutSeconds = 30;
    private readonly SemaphoreSlim _natLock = new(1, 1);
    private static readonly Regex _interfaceNamePattern = new("^[A-Za-z0-9_.-]{1,15}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _natCommentPattern = new("^kvmcontrol:(?<id>[a-f0-9]{32}):(?<protocol>tcp|udp):(?<listenAddress>[^:]+):(?<listenPort>\\d+):(?<targetAddress>[^:]+):(?<targetPort>\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<HostBridge>> ListBridgesAsync(CancellationToken cancellationToken)
    {
        List<HostBridge> bridges = await ListLinuxBridgesAsync(cancellationToken);
        bridges.AddRange(await ListOpenVSwitchBridgesAsync(cancellationToken));
        return bridges.OrderBy(bridge => bridge.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<HostBridge> CreateBridgeAsync(string name, HostBridgeType type, IReadOnlyList<string> ports, CancellationToken cancellationToken)
    {
        ValidateManagedBridgeName(name);
        ValidatePorts(ports);

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
                    await RunAsync("ip", ["link", "set", "dev", name, "alias", "kvmcontrol"], cancellationToken);
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
                    await RunAsync("ovs-vsctl", ["set", "bridge", name, "external_ids:kvmcontrol=managed"], cancellationToken);
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

    public async Task DeleteBridgeAsync(string name, HostBridgeType type, CancellationToken cancellationToken)
    {
        ValidateManagedBridgeName(name);
        if (!await IsManagedBridgeAsync(name, type, cancellationToken))
        {
            throw new InvalidOperationException("Bridge is not managed by KvmControl.");
        }
        await (type switch
        {
            HostBridgeType.Linux => RunAsync("ip", ["link", "delete", "dev", name, "type", "bridge"], cancellationToken),
            HostBridgeType.OpenVSwitch => RunAsync("ovs-vsctl", ["del-br", name], cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        });
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
        ValidateNatAddress(listenAddress, options.Value.AllowedNatListenCidrs, nameof(listenAddress));
        ValidateNatAddress(targetAddress, options.Value.AllowedNatTargetCidrs, nameof(targetAddress));
        string id = Guid.NewGuid().ToString("N");
        string comment = $"kvmcontrol:{id}:{protocol}:{listenAddress}:{listenPort}:{targetAddress}:{targetPort}";

        await _natLock.WaitAsync(cancellationToken);
        try
        {
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
        }
        finally
        {
            _natLock.Release();
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
            if (!bridge.TryGetProperty("ifalias", out JsonElement alias) || !string.Equals(alias.GetString(), "kvmcontrol", StringComparison.Ordinal))
            {
                continue;
            }
            CommandResult portsResult = await TryRunAsync("bridge", ["--json", "link", "show", "master", name], cancellationToken);
            string[] ports = portsResult.ExitCode == 0
                ? GetInterfaceNames(portsResult.StandardOutput)
                : [];
            bool up = string.Equals(bridge.GetProperty("operstate").GetString(), "UP", StringComparison.OrdinalIgnoreCase);
            bridges.Add(new HostBridge(name, HostBridgeType.Linux, ports, up));
        }

        return bridges;
    }

    private async Task<List<HostBridge>> ListOpenVSwitchBridgesAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await TryRunAsync("ovs-vsctl", ["list-br"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        List<HostBridge> bridges = [];
        foreach (string name in result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!await IsManagedBridgeAsync(name, HostBridgeType.OpenVSwitch, cancellationToken))
            {
                continue;
            }
            CommandResult portsResult = await TryRunAsync("ovs-vsctl", ["list-ports", name], cancellationToken);
            CommandResult linkResult = await TryRunAsync("ip", ["--json", "link", "show", "dev", name], cancellationToken);
            bool up = linkResult.ExitCode == 0 && IsLinkUp(linkResult.StandardOutput);
            bridges.Add(new HostBridge(name, HostBridgeType.OpenVSwitch, portsResult.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), up));
        }

        return bridges;
    }

    /// <summary>确保受管 nft 表存在，并收敛 forward 链的 policy 与定向隔离规则。</summary>
    private async Task EnsureNatTableAsync(CancellationToken cancellationToken)
    {
        CommandResult table = await TryRunAsync("nft", ["list", "table", "ip", NatTable], cancellationToken);
        if (table.ExitCode != 0)
        {
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

        await EnsureForwardChainPolicyAsync(cancellationToken);
        await EnsureForwardIsolationAsync(cancellationToken);
    }

    private async Task EnsureForwardChainPolicyAsync(CancellationToken cancellationToken)
    {
        // policy 必须保持 accept：同一 hook 的多个 base chain 都会被求值且 drop 优先，
        // 无条件 drop 会连宿主机自身链（Docker、libvirt 等）已放行的转发一起丢掉。
        // 重复下发 chain 定义即更新 policy，可顺带修复旧版本建出的表。
        await RunAsync("nft", ["add", "chain", "ip", NatTable, "forward", "{", "type", "filter", "hook", "forward", "priority", "filter;", "policy", "accept;", "}"], cancellationToken);
    }

    private async Task EnsureForwardIsolationAsync(CancellationToken cancellationToken)
    {
        // 不用整链 drop 实现隔离：先放行 conntrack DNAT 与 established/related，
        // 再对直接进入受管 NAT 目标网段的新连接 drop；其余流量落回宿主机自身转发链。
        // accept/drop 只结束本链的判定，不会绕过宿主机防火墙。
        List<(string Comment, string[] Arguments)> desired =
        [
            ("kvmcontrol:forward:dnat-accept", ["ct", "status", "dnat", "accept"]),
            ("kvmcontrol:forward:established-accept", ["ct", "state", "established", "accept"]),
            ("kvmcontrol:forward:related-accept", ["ct", "state", "related", "accept"])
        ];
        foreach (string cidr in options.Value.AllowedNatTargetCidrs)
        {
            if (TryNormalizeCidr(cidr, out string normalized))
            {
                desired.Add(($"kvmcontrol:forward:isolate:{normalized}", ["ip", "daddr", normalized, "drop"]));
            }
        }

        Dictionary<string, List<int>> existing = await GetForwardIsolationRuleHandlesAsync(cancellationToken);
        foreach ((string comment, List<int> handles) in existing)
        {
            if (desired.All(rule => rule.Comment != comment))
            {
                foreach (int handle in handles)
                {
                    await RunAsync("nft", ["delete", "rule", "ip", NatTable, "forward", "handle", handle.ToString()], cancellationToken);
                }
            }
        }
        foreach ((string comment, string[] arguments) in desired)
        {
            if (!existing.ContainsKey(comment))
            {
                await RunAsync("nft", ["add", "rule", "ip", NatTable, "forward", .. arguments, "comment", comment], cancellationToken);
            }
        }
    }

    private async Task<Dictionary<string, List<int>>> GetForwardIsolationRuleHandlesAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await TryRunAsync("nft", ["--json", "--handle", "list", "chain", "ip", NatTable, "forward"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new HostNetworkCommandException("nft", result.ExitCode, result.StandardError.Trim());
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Dictionary<string, List<int>> handles = new(StringComparer.Ordinal);
        foreach (JsonElement item in document.RootElement.GetProperty("nftables").EnumerateArray())
        {
            if (!item.TryGetProperty("rule", out JsonElement rule)
                || !rule.TryGetProperty("comment", out JsonElement comment)
                || !comment.GetString()!.StartsWith("kvmcontrol:forward:", StringComparison.Ordinal))
            {
                continue;
            }
            List<int> commentHandles = handles.TryGetValue(comment.GetString()!, out List<int>? list) ? list : [];
            commentHandles.Add(rule.GetProperty("handle").GetInt32());
            handles[comment.GetString()!] = commentHandles;
        }
        return handles;
    }

    private static bool TryNormalizeCidr(string cidr, out string normalized)
    {
        normalized = string.Empty;
        string[] parts = cidr.Split('/', 2);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out int prefixLength)
            || prefixLength is < 0 or > 32)
        {
            return false;
        }
        normalized = $"{address}/{prefixLength}";
        return true;
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

            Match match = _natCommentPattern.Match(comment.GetString() ?? string.Empty);
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

    private async Task<bool> IsManagedBridgeAsync(string name, HostBridgeType type, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case HostBridgeType.Linux:
                CommandResult link = await TryRunAsync("ip", ["--json", "link", "show", "dev", name], cancellationToken);
                if (link.ExitCode != 0)
                {
                    return false;
                }
                using (JsonDocument document = JsonDocument.Parse(link.StandardOutput))
                {
                    return document.RootElement.EnumerateArray().Any(item => item.TryGetProperty("ifalias", out JsonElement alias) && alias.GetString() == "kvmcontrol");
                }
            case HostBridgeType.OpenVSwitch:
                CommandResult ovs = await TryRunAsync("ovs-vsctl", ["get", "bridge", name, "external_ids:kvmcontrol"], cancellationToken);
                return ovs.ExitCode == 0 && string.Equals(ovs.StandardOutput.Trim().Trim('"'), "managed", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private void ValidateManagedBridgeName(string name)
    {
        ValidateInterfaceName(name);
        string[] prefixes = options.Value.AllowedBridgePrefixes;
        if (prefixes.Length == 0 || !prefixes.Any(prefix => !string.IsNullOrWhiteSpace(prefix) && name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Bridge name is not within the managed bridge prefixes.", nameof(name));
        }
    }

    private void ValidatePorts(IReadOnlyList<string> ports)
    {
        int maximumPorts = Math.Clamp(options.Value.MaximumBridgePorts, 0, 256);
        if (ports.Count > maximumPorts || ports.Distinct(StringComparer.Ordinal).Count() != ports.Count)
        {
            throw new ArgumentException("Bridge port count is invalid or contains duplicates.", nameof(ports));
        }
        HashSet<string> allowedPorts = options.Value.AllowedPorts.ToHashSet(StringComparer.Ordinal);
        foreach (string port in ports)
        {
            ValidateInterfaceName(port);
            if (!allowedPorts.Contains(port))
            {
                throw new ArgumentException("Bridge port is not approved by the node configuration.", nameof(ports));
            }
        }
    }

    private static void ValidateNatAddress(string address, IReadOnlyList<string> allowedCidrs, string parameterName)
    {
        if (!IPAddress.TryParse(address, out IPAddress? parsedAddress) || allowedCidrs.Count == 0 || !allowedCidrs.Any(cidr => IsInCidr(parsedAddress, cidr)))
        {
            throw new ArgumentException("NAT address is outside the approved CIDR ranges.", parameterName);
        }
    }

    private static bool IsInCidr(IPAddress address, string cidr)
    {
        string[] parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? network) || !int.TryParse(parts[1], out int prefixLength)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || prefixLength is < 0 or > 32)
        {
            return false;
        }

        uint addressValue = BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray());
        uint networkValue = BitConverter.ToUInt32(network.GetAddressBytes().Reverse().ToArray());
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return (addressValue & mask) == (networkValue & mask);
    }

    private static void ValidateInterfaceName(string name)
    {
        if (!_interfaceNamePattern.IsMatch(name))
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
        try
        {
            await process.WaitForExitAsync(linkedTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit();
            }
            throw;
        }
        return new CommandResult(process.ExitCode, await standardOutput, await standardError);
    }

}
