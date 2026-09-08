using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Cluster.Services;

public sealed class VirtualMachineProvisioner(IOptions<Models.Options.ProvisioningOptions> options, IVirshClient virshClient) : IVirtualMachineProvisioner
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MacAddressPattern = new("^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UserNamePattern = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SshKeyPattern = new("^(ssh-(ed25519|rsa)|ecdsa-sha2-[A-Za-z0-9-]+) [A-Za-z0-9+/=]+(?: [^\\r\\n]*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SearchDomainPattern = new("^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> VirtualMachineLocks = new(StringComparer.Ordinal);
    private const int CommandTimeoutSeconds = 120;
    private const string ManagedMarkerFileName = ".managed-by-kvmcontrol";

    public async Task<VirshVirtualMachine> CreateAsync(VirtualMachineProvisionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        SemaphoreSlim virtualMachineLock = VirtualMachineLocks.GetOrAdd(request.Name, _ => new SemaphoreSlim(1, 1));
        await virtualMachineLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateLockedAsync(request, cancellationToken);
        }
        finally
        {
            virtualMachineLock.Release();
        }
    }

    private async Task<VirshVirtualMachine> CreateLockedAsync(VirtualMachineProvisionRequest request, CancellationToken cancellationToken)
    {
        Models.Options.ProvisioningOptions configuration = options.Value;
        string baseImage = ResolveBaseImage(configuration.BaseImageDirectory, request.BaseImage);
        string virtualMachineDirectory = Path.Combine(Path.GetFullPath(configuration.VirtualMachineDirectory), request.Name);
        string varsPath = Path.Combine(virtualMachineDirectory, "OVMF_VARS.fd");
        string diskPath = Path.Combine(virtualMachineDirectory, "disk.qcow2");
        string cloudInitPath = Path.Combine(virtualMachineDirectory, "cloud-init.iso");
        string domainXmlPath = Path.Combine(virtualMachineDirectory, "domain.xml");

        if (await virshClient.GetVirtualMachineAsync(request.Name, cancellationToken) is not null)
        {
            throw new InvalidOperationException($"Virtual machine '{request.Name}' already exists.");
        }
        if (Directory.Exists(virtualMachineDirectory))
        {
            throw new InvalidOperationException($"Virtual machine storage '{request.Name}' already exists.");
        }
        if (!File.Exists(configuration.OvmfCodePath) || !File.Exists(configuration.OvmfVarsTemplatePath))
        {
            throw new InvalidOperationException("Configured OVMF firmware files are not available.");
        }

        Directory.CreateDirectory(virtualMachineDirectory);
        try
        {
            File.WriteAllText(Path.Combine(virtualMachineDirectory, ManagedMarkerFileName), request.Name);
            File.Copy(configuration.OvmfVarsTemplatePath, varsPath);
            await RunAsync("qemu-img", ["create", "-f", "qcow2", "-b", baseImage, "-F", "qcow2", diskPath], cancellationToken);
            await CreateCloudInitImageAsync(cloudInitPath, request, cancellationToken);
            string domainXml = CreateDomainXml(request, configuration.OvmfCodePath, varsPath, diskPath, cloudInitPath);
            await File.WriteAllTextAsync(domainXmlPath, domainXml, new UTF8Encoding(false), cancellationToken);
            await virshClient.DefineAsync(domainXmlPath, cancellationToken);

            if (request.Start)
            {
                await virshClient.ChangePowerStateAsync(request.Name, VirshPowerAction.Start, cancellationToken);
            }

            return await virshClient.GetVirtualMachineAsync(request.Name, cancellationToken)
                ?? throw new InvalidOperationException("Virtual machine was not found after definition.");
        }
        catch
        {
            try
            {
                await virshClient.UndefineAsync(request.Name, CancellationToken.None);
            }
            catch
            {
            }
            if (Directory.Exists(virtualMachineDirectory))
            {
                Directory.Delete(virtualMachineDirectory, true);
            }
            throw;
        }
    }

    public async Task DeleteAsync(string name, bool force, bool deleteStorage, CancellationToken cancellationToken)
    {
        if (!NamePattern.IsMatch(name))
        {
            throw new ArgumentException("Virtual machine name is invalid.", nameof(name));
        }

        SemaphoreSlim virtualMachineLock = VirtualMachineLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await virtualMachineLock.WaitAsync(cancellationToken);
        try
        {
            await DeleteLockedAsync(name, force, deleteStorage, cancellationToken);
        }
        finally
        {
            virtualMachineLock.Release();
        }
    }

    private async Task DeleteLockedAsync(string name, bool force, bool deleteStorage, CancellationToken cancellationToken)
    {
        VirshVirtualMachine? virtualMachine = await virshClient.GetVirtualMachineAsync(name, cancellationToken);
        if (virtualMachine is not null)
        {
            if (!await virshClient.IsManagedAsync(name, cancellationToken))
            {
                throw new InvalidOperationException("Virtual machine is not managed by KvmControl.");
            }
            {
                if (!force)
                {
                    throw new InvalidOperationException("Virtual machine must be shut off before deletion, or force must be enabled.");
                }
                await virshClient.ChangePowerStateAsync(name, VirshPowerAction.ForceOff, cancellationToken);
            }

            await virshClient.UndefineAsync(name, cancellationToken);
        }
        if (!deleteStorage)
        {
            return;
        }

        DeleteManagedStorage(name);
    }

    private void DeleteManagedStorage(string name)
    {
        string root = Path.GetFullPath(options.Value.VirtualMachineDirectory);
        string virtualMachineDirectory = Path.GetFullPath(Path.Combine(root, name));
        if (!virtualMachineDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Virtual machine storage is outside the managed directory.");
        }
        if (Directory.Exists(virtualMachineDirectory))
        {
            string markerPath = Path.Combine(virtualMachineDirectory, ManagedMarkerFileName);
            if (!File.Exists(markerPath) || !string.Equals(File.ReadAllText(markerPath), name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Virtual machine storage is not managed by KvmControl.");
            }
            Directory.Delete(virtualMachineDirectory, true);
        }
    }

    private static async Task CreateCloudInitImageAsync(string path, VirtualMachineProvisionRequest request, CancellationToken cancellationToken)
    {
        string stagingDirectory = Path.Combine(Path.GetDirectoryName(path)!, ".cloud-init");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            string userData = CreateUserData(request);
            string networkConfig = CreateNetworkConfig(request);
            string instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userData}\n{networkConfig}"))).ToLowerInvariant();
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, "user-data"), userData, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, "network-config"), networkConfig, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, "meta-data"), $"instance-id: {instanceId}\nlocal-hostname: {request.Name}\n", new UTF8Encoding(false), cancellationToken);
            await RunAsync("genisoimage", ["-output", path, "-volid", "cidata", "-joliet", "-rock", Path.Combine(stagingDirectory, "user-data"), Path.Combine(stagingDirectory, "meta-data"), Path.Combine(stagingDirectory, "network-config")], cancellationToken);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }
        }
    }

    private static string CreateUserData(VirtualMachineProvisionRequest request)
    {
        StringBuilder builder = new("#cloud-config\n");
        builder.AppendLine("users:");
        builder.AppendLine("  - default");
        builder.AppendLine($"  - name: {request.CloudInit.UserName}");
        builder.AppendLine("    sudo: ALL=(ALL) NOPASSWD:ALL");
        builder.AppendLine("    shell: /bin/bash");
        builder.AppendLine("    ssh_authorized_keys:");
        foreach (string key in request.CloudInit.SshAuthorizedKeys)
        {
            builder.AppendLine($"      - {key}");
        }
        builder.AppendLine("ssh_pwauth: false");
        builder.AppendLine("disable_root: true");
        return builder.ToString();
    }

    private static string CreateNetworkConfig(VirtualMachineProvisionRequest request)
    {
        CloudInitConfiguration cloudInit = request.CloudInit;
        StringBuilder builder = new("version: 2\nethernets:\n");
        builder.AppendLine("  eth0:");
        builder.AppendLine($"    match:\n      macaddress: '{request.MacAddress.ToLowerInvariant()}'");
        builder.AppendLine("    set-name: eth0");
        if (string.Equals(cloudInit.Ipv4Address, "dhcp", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("    dhcp4: true");
        }
        else
        {
            builder.AppendLine("    dhcp4: false");
            builder.AppendLine($"    addresses: ['{cloudInit.Ipv4Address}']");
            builder.AppendLine($"    routes:\n      - to: default\n        via: {cloudInit.Ipv4Gateway}");
        }
        if (cloudInit.DnsServers.Count > 0)
        {
            builder.AppendLine("    nameservers:");
            builder.AppendLine($"      addresses: [{string.Join(", ", cloudInit.DnsServers)}]");
            if (!string.IsNullOrEmpty(cloudInit.SearchDomain))
            {
                builder.AppendLine($"      search: [{cloudInit.SearchDomain}]");
            }
        }
        return builder.ToString();
    }

    private static string CreateDomainXml(VirtualMachineProvisionRequest request, string codePath, string varsPath, string diskPath, string cloudInitPath)
    {
        XmlWriterSettings settings = new() { Indent = true, OmitXmlDeclaration = true };
        StringBuilder output = new();
        using XmlWriter writer = XmlWriter.Create(output, settings);
        writer.WriteStartElement("domain");
        writer.WriteAttributeString("type", "kvm");
        writer.WriteElementString("name", request.Name);
        writer.WriteElementString("memory", (request.MemoryMiB * 1024).ToString());
        writer.WriteElementString("currentMemory", (request.MemoryMiB * 1024).ToString());
        writer.WriteElementString("vcpu", request.VirtualCpuCount.ToString());
        writer.WriteStartElement("os");
        writer.WriteStartElement("type");
        writer.WriteAttributeString("arch", "x86_64");
        writer.WriteAttributeString("machine", "q35");
        writer.WriteString("hvm");
        writer.WriteEndElement();
        writer.WriteStartElement("loader");
        writer.WriteAttributeString("readonly", "yes");
        writer.WriteAttributeString("type", "pflash");
        writer.WriteString(codePath);
        writer.WriteEndElement();
        writer.WriteElementString("nvram", varsPath);
        writer.WriteEndElement();
        writer.WriteStartElement("features");
        writer.WriteElementString("acpi", string.Empty);
        writer.WriteEndElement();
        writer.WriteStartElement("cpu");
        writer.WriteAttributeString("mode", "host-passthrough");
        writer.WriteEndElement();
        writer.WriteStartElement("devices");
        writer.WriteElementString("emulator", "/usr/bin/qemu-system-x86_64");
        WriteDisk(writer, diskPath, "disk", "virtio", "vda");
        WriteDisk(writer, cloudInitPath, "cdrom", "sata", "sda");
        writer.WriteStartElement("interface");
        writer.WriteAttributeString("type", "bridge");
        writer.WriteStartElement("mac");
        writer.WriteAttributeString("address", request.MacAddress);
        writer.WriteEndElement();
        writer.WriteStartElement("source");
        writer.WriteAttributeString("bridge", request.BridgeName);
        writer.WriteEndElement();
        writer.WriteStartElement("model");
        writer.WriteAttributeString("type", "virtio");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("graphics");
        writer.WriteAttributeString("type", "vnc");
        writer.WriteAttributeString("autoport", "yes");
        writer.WriteAttributeString("listen", "127.0.0.1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Flush();
        return output.ToString();
    }

    private static void WriteDisk(XmlWriter writer, string path, string device, string bus, string target)
    {
        writer.WriteStartElement("disk");
        writer.WriteAttributeString("type", "file");
        writer.WriteAttributeString("device", device);
        writer.WriteStartElement("driver");
        writer.WriteAttributeString("name", "qemu");
        writer.WriteAttributeString("type", device == "disk" ? "qcow2" : "raw");
        writer.WriteEndElement();
        writer.WriteStartElement("source");
        writer.WriteAttributeString("file", path);
        writer.WriteEndElement();
        writer.WriteStartElement("target");
        writer.WriteAttributeString("dev", target);
        writer.WriteAttributeString("bus", bus);
        writer.WriteEndElement();
        if (device == "cdrom")
        {
            writer.WriteElementString("readonly", string.Empty);
        }
        writer.WriteEndElement();
    }

    private static string ResolveBaseImage(string baseImageDirectory, string baseImage)
    {
        if (!NamePattern.IsMatch(baseImage) || !baseImage.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Base image must be an approved qcow2 filename.", nameof(baseImage));
        }

        string root = Path.GetFullPath(baseImageDirectory);
        string path = Path.GetFullPath(Path.Combine(root, baseImage));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            throw new ArgumentException("Base image was not found in the approved image directory.", nameof(baseImage));
        }

        return path;
    }

    private static void ValidateRequest(VirtualMachineProvisionRequest request)
    {
        if (!NamePattern.IsMatch(request.Name))
        {
            throw new ArgumentException("Virtual machine name is invalid.", nameof(request));
        }
        if (request.MemoryMiB is < 512 or > 1_048_576 || request.VirtualCpuCount is 0 or > 256)
        {
            throw new ArgumentException("Memory must be 512-1048576 MiB and virtual CPU count must be 1-256.", nameof(request));
        }
        if (!NamePattern.IsMatch(request.BridgeName) || !MacAddressPattern.IsMatch(request.MacAddress))
        {
            throw new ArgumentException("Bridge name or MAC address is invalid.", nameof(request));
        }
        if (!UserNamePattern.IsMatch(request.CloudInit.UserName) || request.CloudInit.SshAuthorizedKeys.Count is 0 or > 32 || request.CloudInit.SshAuthorizedKeys.Any(key => key.Length > 8192 || !SshKeyPattern.IsMatch(key)))
        {
            throw new ArgumentException("Cloud-init user or SSH public keys are invalid.", nameof(request));
        }
        if (!string.Equals(request.CloudInit.Ipv4Address, "dhcp", StringComparison.OrdinalIgnoreCase))
        {
            string[] addressParts = request.CloudInit.Ipv4Address.Split('/', 2);
            if (addressParts.Length != 2 || !IPAddress.TryParse(addressParts[0], out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !byte.TryParse(addressParts[1], out byte prefix) || prefix > 32 || !IPAddress.TryParse(request.CloudInit.Ipv4Gateway, out IPAddress? gateway) || gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Static IPv4 cloud-init configuration is invalid.", nameof(request));
            }
        }
        if (request.CloudInit.DnsServers.Any(server => !IPAddress.TryParse(server, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            throw new ArgumentException("Cloud-init DNS servers must be IPv4 addresses.", nameof(request));
        }
        if (!string.IsNullOrEmpty(request.CloudInit.SearchDomain) && !SearchDomainPattern.IsMatch(request.CloudInit.SearchDomain))
        {
            throw new ArgumentException("Cloud-init search domain is invalid.", nameof(request));
        }
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
        Task<string> error = process.StandardError.ReadToEndAsync(linkedTokenSource.Token);
        await process.WaitForExitAsync(linkedTokenSource.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {(await error).Trim()}");
        }
    }
}
