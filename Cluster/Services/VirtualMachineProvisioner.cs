using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Cluster.Interface;

namespace Cluster.Services;

/// <summary>虚拟机生命周期管理：创建（qcow2/cloud-init/domain XML）与删除，全部操作仅作用于带受管标记的 VM。</summary>
public sealed class VirtualMachineProvisioner(
    IOptions<Models.Options.ProvisioningOptions> options,
    IVirshClient virshClient,
    IVirtualMachineLockManager virtualMachineLockManager,
    ILogger<VirtualMachineProvisioner> logger) : IVirtualMachineProvisioner
{
    private static readonly Regex _namePattern = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _macAddressPattern = new("^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _userNamePattern = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _sshKeyPattern = new("^(ssh-(ed25519|rsa)|ecdsa-sha2-[A-Za-z0-9-]+) [A-Za-z0-9+/=]+(?: [^\\r\\n]*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _searchDomainPattern = new("^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim _provisionLock = new(1, 1);
    private const int CommandTimeoutSeconds = 120;
    private const string ManagedMarkerFileName = ".managed-by-kvmcontrol";

    public async Task<VirshVirtualMachine> CreateAsync(VirtualMachineProvisionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        return await virtualMachineLockManager.RunAsync(request.Name, async _ =>
        {
            await _provisionLock.WaitAsync(cancellationToken);
            try
            {
                return await CreateLockedAsync(request, cancellationToken);
            }
            finally
            {
                _provisionLock.Release();
            }
        }, cancellationToken);
    }

    private async Task<VirshVirtualMachine> CreateLockedAsync(VirtualMachineProvisionRequest request, CancellationToken cancellationToken)
    {
        Models.Options.ProvisioningOptions configuration = options.Value;
        ValidateConfiguredLimits(request, configuration);
        string baseImage = ResolveBaseImage(configuration.BaseImageDirectory, request.BaseImage);
        string virtualMachineDirectory = Path.Combine(Path.GetFullPath(configuration.VirtualMachineDirectory), request.Name);
        string varsPath = Path.Combine(virtualMachineDirectory, "OVMF_VARS.fd");
        string diskPath = Path.Combine(virtualMachineDirectory, "disk.qcow2");
        string cloudInitPath = Path.Combine(virtualMachineDirectory, "cloud-init.iso");
        string domainXmlPath = Path.Combine(virtualMachineDirectory, "domain.xml");
        string domainUuid = Guid.NewGuid().ToString();

        if (await virshClient.ListVirtualMachinesAsync(cancellationToken) is { Count: var count } && count >= Math.Clamp(configuration.MaximumVirtualMachines, 1, 4096))
        {
            throw new InvalidOperationException("The managed virtual machine limit has been reached.");
        }
        if (await virshClient.DomainExistsAsync(request.Name, cancellationToken))
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
        bool ownsDirectory = true;
        string markerPath = Path.Combine(virtualMachineDirectory, ManagedMarkerFileName);
        try
        {
            SetDirectoryPermissions(virtualMachineDirectory);
            await using (FileStream marker = new(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (StreamWriter writer = new(marker, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(domainUuid);
            }
            SetFilePermissions(markerPath);
            File.Copy(configuration.OvmfVarsTemplatePath, varsPath);
            SetFilePermissions(varsPath);
            await RunAsync("qemu-img", ["create", "-f", "qcow2", "-b", baseImage, "-F", "qcow2", diskPath], cancellationToken);
            SetFilePermissions(diskPath);
            await CreateCloudInitImageAsync(cloudInitPath, request, cancellationToken);
            SetFilePermissions(cloudInitPath);
            string domainXml = CreateDomainXml(request, domainUuid, configuration.OvmfCodePath, varsPath, diskPath, cloudInitPath);
            await File.WriteAllTextAsync(domainXmlPath, domainXml, new UTF8Encoding(false), cancellationToken);
            SetFilePermissions(domainXmlPath);
            await virshClient.DefineAsync(domainXmlPath, cancellationToken);

            if (request.Start)
            {
                await virshClient.ChangePowerStateAsync(request.Name, VirshPowerAction.Start, cancellationToken);
            }

            return await virshClient.GetVirtualMachineAsync(request.Name, cancellationToken)
                ?? throw new InvalidOperationException("Virtual machine was not found after definition.");
        }
        catch (Exception exception)
        {
            using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(CommandTimeoutSeconds));
            bool domainRemoved = false;
            try
            {
                domainRemoved = await virshClient.UndefineIfUuidMatchesAsync(request.Name, domainUuid, cleanupTimeout.Token);
            }
            catch (Exception cleanupException)
            {
                logger.LogError(cleanupException, "Unable to remove failed virtual machine definition {VirtualMachineName} ({VirtualMachineUuid}).", request.Name, domainUuid);
            }

            if (domainRemoved && ownsDirectory && Directory.Exists(virtualMachineDirectory)
                && (!File.Exists(markerPath)
                    || string.Equals(File.ReadAllText(markerPath).Trim(), domainUuid, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    Directory.Delete(virtualMachineDirectory, true);
                }
                catch (Exception cleanupException)
                {
                    logger.LogError(cleanupException, "Unable to remove failed virtual machine storage {VirtualMachineDirectory}.", virtualMachineDirectory);
                }
            }
            else if (ownsDirectory && !domainRemoved)
            {
                logger.LogWarning("Preserving virtual machine storage {VirtualMachineDirectory} because the failed domain could not be confirmed as undefined.", virtualMachineDirectory);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
            throw new UnreachableException();
        }
    }

    public async Task DeleteAsync(string name, bool force, bool deleteStorage, CancellationToken cancellationToken)
    {
        if (!_namePattern.IsMatch(name))
        {
            throw new ArgumentException("Virtual machine name is invalid.", nameof(name));
        }

        await virtualMachineLockManager.RunAsync(name, _ => DeleteLockedAsync(name, force, deleteStorage, cancellationToken), cancellationToken);
    }

    private async Task DeleteLockedAsync(string name, bool force, bool deleteStorage, CancellationToken cancellationToken)
    {
        bool domainExists = await virshClient.DomainExistsAsync(name, cancellationToken);
        if (domainExists && !await virshClient.IsManagedAsync(name, cancellationToken))
        {
            throw new InvalidOperationException("Virtual machine is not managed by KvmControl.");
        }
        string? managedUuid = null;
        if (domainExists)
        {
            VirshVirtualMachine domain = await virshClient.GetVirtualMachineAsync(name, cancellationToken)
                ?? throw new InvalidOperationException("Virtual machine is not managed by KvmControl.");
            managedUuid = domain.Uuid;
        }
        VirshVirtualMachine? virtualMachine = domainExists
            ? await virshClient.GetVirtualMachineAsync(name, cancellationToken)
            : null;
        if (virtualMachine is not null)
        {
            if (virtualMachine.State != VirshVirtualMachineState.Shutoff)
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

        DeleteManagedStorage(name, managedUuid);
    }

    private void DeleteManagedStorage(string name, string? managedUuid)
    {
        string root = Path.GetFullPath(options.Value.VirtualMachineDirectory);
        string virtualMachineDirectory = Path.GetFullPath(Path.Combine(root, name));
        if (!virtualMachineDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Virtual machine storage is outside the managed directory.");
        }
        if (Directory.Exists(virtualMachineDirectory))
        {
            DirectoryInfo directoryInfo = new(virtualMachineDirectory);
            if (directoryInfo.LinkTarget is not null || (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Symbolic-link virtual machine storage is not allowed.");
            }
            string markerPath = Path.Combine(virtualMachineDirectory, ManagedMarkerFileName);
            if (!File.Exists(markerPath))
            {
                return;
            }
            string marker = File.ReadAllText(markerPath).Trim();
            if (!Guid.TryParse(marker, out _)
                || (managedUuid is not null && !string.Equals(marker, managedUuid, StringComparison.OrdinalIgnoreCase)))
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
            if (userData.Length + networkConfig.Length > 3 * 1024 * 1024)
            {
                throw new ArgumentException("Cloud-init data exceeds the maximum seed size.", nameof(request));
            }
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
            builder.AppendLine($"      - '{key.Replace("'", "''", StringComparison.Ordinal)}'");
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

    private static string CreateDomainXml(VirtualMachineProvisionRequest request, string domainUuid, string codePath, string varsPath, string diskPath, string cloudInitPath)
    {
        XmlWriterSettings settings = new() { Indent = true, OmitXmlDeclaration = true };
        StringBuilder output = new();
        using XmlWriter writer = XmlWriter.Create(output, settings);
        writer.WriteStartElement("domain");
        writer.WriteAttributeString("type", "kvm");
        writer.WriteElementString("name", request.Name);
        writer.WriteElementString("uuid", domainUuid);
        writer.WriteStartElement("metadata");
        writer.WriteStartElement("managed", "urn:kvmcontrol:managed");
        writer.WriteAttributeString("version", "1");
        writer.WriteString(domainUuid);
        writer.WriteEndElement();
        writer.WriteEndElement();
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
        writer.WriteAttributeString("passwd", Convert.ToBase64String(RandomNumberGenerator.GetBytes(6)));
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
        if (!_namePattern.IsMatch(baseImage) || !baseImage.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Base image must be an approved qcow2 filename.", nameof(baseImage));
        }

        string root = Path.GetFullPath(baseImageDirectory);
        string path = Path.GetFullPath(Path.Combine(root, baseImage));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            throw new ArgumentException("Base image was not found in the approved image directory.", nameof(baseImage));
        }

        EnsureNoSymbolicLinks(root, path);
        return path;
    }

    private static void EnsureNoSymbolicLinks(string root, string path)
    {
        FileSystemInfo current = new DirectoryInfo(root);
        if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Symbolic links are not allowed in the base image path.", nameof(path));
        }

        string relative = Path.GetRelativePath(root, path);
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current is DirectoryInfo directory
                ? (FileSystemInfo)new FileInfo(Path.Combine(directory.FullName, component))
                : throw new ArgumentException("Base image path is invalid.", nameof(path));
            if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Symbolic links are not allowed in the base image path.", nameof(path));
            }
        }
    }

    private static void ValidateConfiguredLimits(VirtualMachineProvisionRequest request, Models.Options.ProvisioningOptions configuration)
    {
        ulong maximumMemoryMiB = Math.Clamp(configuration.MaximumMemoryMiB, 512, 1_048_576);
        uint maximumVirtualCpuCount = Math.Clamp(configuration.MaximumVirtualCpuCount, 1, 256);
        if (request.MemoryMiB > maximumMemoryMiB || request.VirtualCpuCount > maximumVirtualCpuCount)
        {
            throw new ArgumentException("Requested virtual machine resources exceed the configured node limits.", nameof(request));
        }
        if (configuration.AllowedBridges.Length == 0 || !configuration.AllowedBridges.Contains(request.BridgeName, StringComparer.Ordinal))
        {
            throw new ArgumentException("The requested bridge is not approved for virtual machines.", nameof(request));
        }
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void ValidateRequest(VirtualMachineProvisionRequest request)
    {
        if (!_namePattern.IsMatch(request.Name))
        {
            throw new ArgumentException("Virtual machine name is invalid.", nameof(request));
        }
        if (request.MemoryMiB is < 512 or > 1_048_576 || request.VirtualCpuCount is 0 or > 256)
        {
            throw new ArgumentException("Memory must be 512-1048576 MiB and virtual CPU count must be 1-256.", nameof(request));
        }
        if (!_namePattern.IsMatch(request.BridgeName) || !_macAddressPattern.IsMatch(request.MacAddress))
        {
            throw new ArgumentException("Bridge name or MAC address is invalid.", nameof(request));
        }
        if (request.CloudInit.DnsServers.Count > 8 || !_userNamePattern.IsMatch(request.CloudInit.UserName) || request.CloudInit.SshAuthorizedKeys.Count is 0 or > 32 || request.CloudInit.SshAuthorizedKeys.Any(key => key.Length > 8192 || !_sshKeyPattern.IsMatch(key)))
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
        if (!string.IsNullOrEmpty(request.CloudInit.SearchDomain) && !_searchDomainPattern.IsMatch(request.CloudInit.SearchDomain))
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
        Task<string> output = process.StandardOutput.ReadToEndAsync(linkedTokenSource.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(linkedTokenSource.Token);
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
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {(await error).Trim()}");
        }
        await output;
    }
}
