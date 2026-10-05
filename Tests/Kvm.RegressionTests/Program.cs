/// KvmControl 回归测试入口：以普通控制台程序验证操作缓存、队列、票据存储与解析器等核心约束，断言失败即退出。
using Control.Model.Options;
using Control.Model.Response;
using Control.Services;
using Cluster.Services;
using Kvm.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

// No test-framework packages are needed. Failure exits nonzero.
// Set KVM_TEST_REDIS to additionally exercise the real Redis Lua / GETDEL paths.
MemoryDistributedCache memory = new(Options.Create(new MemoryDistributedCacheOptions()));
await RunAsync("memory", memory, null);
await RunVirtualMachineConfigurationTestsAsync();
await RunNodeMetricsTestsAsync();
string? connection = Environment.GetEnvironmentVariable("KVM_TEST_REDIS");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.WriteLine("SKIP Redis integration: set KVM_TEST_REDIS to a disposable test Redis connection.");
}
else
{
    using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(connection);
    await RunAsync("redis", memory, redis);
}

static async Task RunAsync(string backend, MemoryDistributedCache memory, IConnectionMultiplexer? redis)
{
    string prefix = $"KvmControlRegression-{Guid.NewGuid():N}";
    var options = Options.Create(new CacheOptions { InstanceName = prefix });
    var cache = new OperationCache(memory, options, new ControlInstance(), redis);
    var tickets = new VncConsoleTicketStore(memory, options, redis);
    var otherCache = redis is null ? cache : new OperationCache(memory, options, new ControlInstance(), redis);
    var otherTickets = redis is null ? tickets : new VncConsoleTicketStore(memory, options, redis);
    string? operationId = null;
    try
    {
        // Direct reservations reproduce callers that all passed the earlier LookupAsync check.
        var reservations = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            (i % 2 == 0 ? cache : otherCache).ReserveAsync("NODE", "vm.create", "same-key", "same-body", CancellationToken.None))));
        operationId = reservations[0].Operation!.Id;
        Assert(reservations.Count(r => r.Result == OperationEnqueueResult.Created) == 1,
            "Exactly one reservation must be Created.");
        Assert(reservations.Count(r => r.Result == OperationEnqueueResult.Existing) == 31,
            "All other reservations must be Existing.");
        Assert(reservations.All(r => r.Operation?.Id == operationId), "All retries must share an operation ID.");
        Console.WriteLine($"PASS {backend}: 32 identical reservations have exactly one winner");

        var repeated = await cache.ReserveAsync("node", "vm.create", "same-key", "same-body", CancellationToken.None);
        Assert(repeated.Result == OperationEnqueueResult.Existing, "A repeated reservation is not a new task.");
        var conflict = await cache.ReserveAsync("node", "vm.create", "same-key", "different-body", CancellationToken.None);
        Assert(conflict.Result == OperationEnqueueResult.Conflict, "Changed payload must conflict.");
        var lookup = await cache.LookupAsync("node", "vm.create", "same-key", "same-body", CancellationToken.None);
        Assert(lookup?.Operation?.Id == operationId, "Lookup must return the original operation.");
        var listed = await cache.ListAsync("node", 10, null, CancellationToken.None);
        Assert(listed.Count == 1 && listed[0].Id == operationId, "The node operation list must contain the reservation.");
        Assert((await cache.ListAsync("other-node", 10, null, CancellationToken.None)).Count == 0, "Operation lists must be isolated by node.");
        Assert((await cache.ListAsync("node", 10, listed[0].CreatedAt, CancellationToken.None)).Count == 0, "The before cursor must exclude the cursor operation.");
        await cache.ReleaseAsync(listed[0], CancellationToken.None);
        Assert((await cache.ListAsync("node", 10, null, CancellationToken.None)).Count == 0, "Released operations must leave the node history.");
        var replacement = await cache.ReserveAsync("node", "vm.create", "same-key", "same-body", CancellationToken.None);
        Assert(replacement.Result == OperationEnqueueResult.Created, "A released reservation must be reusable.");
        operationId = replacement.Operation!.Id;
        Console.WriteLine($"PASS {backend}: retry, case normalization, payload conflict, lookup and node history");

        var ticket = await tickets.CreateAsync("node", "test-vm", "test-session", "test-password", CancellationToken.None);
        var consumed = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => (i % 2 == 0 ? tickets : otherTickets).TakeAsync(ticket.Token, CancellationToken.None))));
        Assert(consumed.Count(t => t is not null) == 1, "Only one reader may consume a VNC ticket.");
        Assert(await tickets.TakeAsync(ticket.Token, CancellationToken.None) is null, "Consumed ticket must not be replayed.");
        Console.WriteLine($"PASS {backend}: VNC ticket has exactly one consumer and rejects replay");
    }
    finally
    {
        // Remove only this run's two known operation keys. Never flush a database.
        if (redis is not null && operationId is not null)
        {
            await redis.GetDatabase().KeyDeleteAsync([
                $"{prefix}:operation:{{{operationId}}}",
                $"{prefix}:idempotency:{{{operationId}}}"
            ]);
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertNear(double actual, double expected, double tolerance, string message)
{
    Assert(!double.IsNaN(actual) && Math.Abs(actual - expected) <= tolerance,
        $"{message} Expected {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}±{tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture)} but got {actual.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    catch (Exception exception)
    {
        throw new InvalidOperationException($"{message} Instead it threw {exception.GetType().Name}.");
    }
    throw new InvalidOperationException($"{message} Nothing was thrown.");
}

static async Task RunVirtualMachineConfigurationTestsAsync()
{
    // dumpxml parsing: persistent definition with unit attributes, a current vcpu attribute and both managed disks.
    const string dumpXml = """
        <domain type='kvm'>
          <name>web-01</name>
          <uuid>12345678-1234-1234-1234-123456789abc</uuid>
          <metadata>
            <kvmcontrol:managed xmlns:kvmcontrol='urn:kvmcontrol:managed' version='1'>12345678-1234-1234-1234-123456789abc</kvmcontrol:managed>
          </metadata>
          <memory unit='KiB'>4194304</memory>
          <currentMemory unit='KiB'>4194304</currentMemory>
          <vcpu current='2'>4</vcpu>
          <os>
            <type arch='x86_64' machine='q35'>hvm</type>
          </os>
          <devices>
            <disk type='file' device='disk'>
              <driver name='qemu' type='qcow2'/>
              <source file='/var/lib/kvmcontrol/virtual-machines/web-01/disk.qcow2'/>
              <target dev='vda' bus='virtio'/>
            </disk>
            <disk type='file' device='cdrom'>
              <driver name='qemu' type='raw'/>
              <source file='/var/lib/kvmcontrol/virtual-machines/web-01/cloud-init.iso'/>
              <target dev='sda' bus='sata'/>
              <readonly/>
            </disk>
          </devices>
        </domain>
        """;
    VirshDomainDefinition definition = VirshDomainXmlParser.Parse(dumpXml);
    Assert(definition.Uuid == "12345678-1234-1234-1234-123456789abc", "The domain UUID must be parsed.");
    Assert(definition.MaximumMemoryKiB == 4194304UL, "The memory element must be parsed in KiB.");
    Assert(definition.CurrentMemoryKiB == 4194304UL, "The currentMemory element must be parsed in KiB.");
    Assert(definition.VirtualCpuCount == 4U, "The persistent vCPU count must come from the vcpu element value.");
    Assert(definition.Disks.Count == 2, "Both disk elements must be parsed.");
    Assert(definition.Disks[0].Device == "disk" && definition.Disks[0].TargetDevice == "vda" && definition.Disks[0].DriverType == "qcow2", "The system disk must keep its device, target and driver type.");
    Assert(definition.Disks[0].SourceFile == "/var/lib/kvmcontrol/virtual-machines/web-01/disk.qcow2", "The system disk source must be parsed.");
    Assert(definition.Disks[1].Device == "cdrom" && definition.Disks[1].TargetDevice == "sda", "The cloud-init cdrom must be recognized.");
    Console.WriteLine("PASS config: dumpxml persistent definition parsing");

    // Memory unit handling and malformed definitions.
    VirshDomainDefinition mibUnit = VirshDomainXmlParser.Parse(dumpXml.Replace("<memory unit='KiB'>4194304</memory>", "<memory unit='MiB'>4096</memory>"));
    Assert(mibUnit.MaximumMemoryKiB == 4194304UL, "A MiB memory unit must convert to KiB.");
    VirshDomainDefinition noUnit = VirshDomainXmlParser.Parse(dumpXml.Replace("<memory unit='KiB'>4194304</memory>", "<memory>2097152</memory>"));
    Assert(noUnit.MaximumMemoryKiB == 2097152UL, "A memory element without a unit defaults to KiB.");
    AssertThrows<InvalidOperationException>(() => VirshDomainXmlParser.Parse("<domain><name>x</name></domain>"), "A domain without memory must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirshDomainXmlParser.Parse("not xml at all"), "Invalid XML must be rejected.");
    Console.WriteLine("PASS config: memory unit conversion and malformed definition rejection");

    // Mutating CPU and memory for redefinition must only touch memory/currentMemory/vcpu.
    string mutated = VirshDomainXmlMutator.WithCpuAndMemory(dumpXml, 8, 8UL * 1024 * 1024);
    VirshDomainDefinition mutatedDefinition = VirshDomainXmlParser.Parse(mutated);
    Assert(mutatedDefinition.VirtualCpuCount == 8U, "The mutated vCPU count must be applied.");
    Assert(mutatedDefinition.MaximumMemoryKiB == 8UL * 1024 * 1024, "The mutated memory must be applied in KiB.");
    Assert(mutatedDefinition.CurrentMemoryKiB == 8UL * 1024 * 1024, "The mutated currentMemory must be applied in KiB.");
    Assert(mutatedDefinition.Uuid == definition.Uuid, "The mutated XML must keep the domain UUID.");
    Assert(mutatedDefinition.Disks.Count == 2 && mutatedDefinition.Disks[0].SourceFile == definition.Disks[0].SourceFile, "The mutated XML must keep the disk definitions.");
    Assert(mutated.Contains("machine=\"q35\"", StringComparison.Ordinal) || mutated.Contains("machine='q35'", StringComparison.Ordinal), "The mutated XML must keep the machine type.");
    Console.WriteLine("PASS config: CPU and memory mutation for domain redefinition");

    // Name and resource validation.
    Assert(VirtualMachineConfigurationPolicy.IsNameValid("web-01"), "A standard name must be accepted.");
    Assert(VirtualMachineConfigurationPolicy.IsNameValid("a".PadRight(63, 'b')), "A 63-character name must be accepted.");
    Assert(!VirtualMachineConfigurationPolicy.IsNameValid("-leading"), "A name must not start with a separator.");
    Assert(!VirtualMachineConfigurationPolicy.IsNameValid("has space"), "A name must not contain spaces.");
    Assert(!VirtualMachineConfigurationPolicy.IsNameValid("../escape"), "A name must not traverse directories.");
    Assert(!VirtualMachineConfigurationPolicy.IsNameValid(""), "An empty name must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(null, null, 262144, 64), "An update without CPU and memory must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(0, null, 262144, 64), "A zero vCPU count must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(257, null, 262144, 64), "A vCPU count above the hard maximum must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(null, 511, 262144, 64), "Memory below 512 MiB must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(null, 1_048_577, 262144, 64), "Memory above the hard maximum must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(null, 262_145, 262_144, 64), "Memory above the configured node limit must be rejected.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateUpdate(65, null, 262_144, 64), "A vCPU count above the configured node limit must be rejected.");
    VirtualMachineConfigurationPolicy.ValidateUpdate(4, 4096, 262_144, 64);
    VirtualMachineConfigurationPolicy.ValidateUpdate(null, 512, 512, 1);
    Console.WriteLine("PASS config: name and resource validation");

    // GiB conversions and shrink rejection.
    Assert(VirtualMachineConfigurationPolicy.GiBToBytes(20) == 20UL * 1024 * 1024 * 1024, "20 GiB must convert to the exact byte count.");
    Assert(VirtualMachineConfigurationPolicy.BytesToGiB(VirtualMachineConfigurationPolicy.GiBToBytes(20)) == 20UL, "A whole-GiB byte count must convert back exactly.");
    Assert(VirtualMachineConfigurationPolicy.BytesToGiB(VirtualMachineConfigurationPolicy.GiBToBytes(20) + 1) == 21UL, "A partial GiB must round up.");
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateDiskGrowth(VirtualMachineConfigurationPolicy.GiBToBytes(20), 0), "A zero disk size must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.ValidateDiskGrowth(VirtualMachineConfigurationPolicy.GiBToBytes(20), 19), "Shrinking the system disk must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.ValidateDiskGrowth(VirtualMachineConfigurationPolicy.GiBToBytes(20) + 123, 20), "A resize that does not grow beyond the current size must be rejected.");
    VirtualMachineConfigurationPolicy.ValidateDiskGrowth(VirtualMachineConfigurationPolicy.GiBToBytes(20), 21);
    AssertThrows<ArgumentException>(() => VirtualMachineConfigurationPolicy.ValidateDiskGrowth(0, VirtualMachineConfigurationPolicy.MaximumDiskGiB + 1), "A disk size above the hard maximum must be rejected.");
    Console.WriteLine("PASS config: GiB conversion and disk shrink rejection");

    // The managed system disk must be exactly the managed disk.qcow2 inside the virtual machine directory.
    string root = Path.Combine(Path.GetTempPath(), "kvmcontrol-tests");
    string virtualMachineDirectory = Path.Combine(root, "web-01");
    string managedDiskPath = Path.Combine(virtualMachineDirectory, "disk.qcow2");
    VirshDomainDisk systemDisk = VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
        [new VirshDomainDisk("disk", "vda", managedDiskPath, "qcow2"), new VirshDomainDisk("cdrom", "sda", Path.Combine(virtualMachineDirectory, "cloud-init.iso"), "raw")],
        virtualMachineDirectory);
    Assert(systemDisk.TargetDevice == "vda" && systemDisk.Device == "disk", "The managed system disk must be the vda disk device.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
        [new VirshDomainDisk("disk", "vda", Path.Combine(root, "other-vm", "disk.qcow2"), "qcow2")], virtualMachineDirectory),
        "A system disk outside the virtual machine directory must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
        [new VirshDomainDisk("disk", "vda", Path.Combine(root, "web-01", "..", "other-vm", "disk.qcow2"), "qcow2")], virtualMachineDirectory),
        "A system disk escaping the virtual machine directory must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
        [new VirshDomainDisk("disk", "vda", managedDiskPath, "raw")], virtualMachineDirectory),
        "A non-qcow2 system disk must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.FindManagedSystemDisk(
        [new VirshDomainDisk("cdrom", "sda", Path.Combine(virtualMachineDirectory, "cloud-init.iso"), "raw")], virtualMachineDirectory),
        "A virtual machine without a vda disk must be rejected.");
    AssertThrows<InvalidOperationException>(() => VirtualMachineConfigurationPolicy.ResolveVirtualMachineDirectory(root, "../escape"),
        "A virtual machine directory outside the managed root must be rejected.");
    Directory.CreateDirectory(virtualMachineDirectory);
    try
    {
        Assert(Path.GetFullPath(VirtualMachineConfigurationPolicy.ResolveVirtualMachineDirectory(root, "web-01")) == Path.GetFullPath(virtualMachineDirectory),
            "A valid virtual machine directory must resolve below the managed root.");
    }
    finally
    {
        Directory.Delete(root, true);
    }
    Console.WriteLine("PASS config: managed system disk verification");

    // Per-virtual-machine lock serialization.
    VirtualMachineLockManager lockManager = new();
    int concurrent = 0;
    int maxConcurrent = 0;
    int completed = 0;
    await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(async () =>
    {
        await lockManager.RunAsync("shared-vm", async _ =>
        {
            // The locked section runs exclusively, so plain arithmetic here is race-free.
            concurrent++;
            if (concurrent > maxConcurrent)
            {
                maxConcurrent = concurrent;
            }
            await Task.Delay(1);
            completed++;
            concurrent--;
        }, CancellationToken.None);
    })));
    Assert(completed == 128, "Every locked section must run to completion.");
    Assert(maxConcurrent == 1, "Operations for the same virtual machine name must be serialized.");
    AssertThrows<InvalidOperationException>(() => throw new InvalidOperationException("expected"), "Sanity: the throw helper must observe the expected exception.");
    Console.WriteLine("PASS config: per-virtual-machine locks serialize operations");
}

static async Task RunNodeMetricsTestsAsync()
{
    // /proc/stat parsing: aggregate cpu line, per-core lines counted as logical CPUs, non-stat lines ignored.
    ProcStatSample before = NodeMetricsParser.ParseProcStat("cpu  10 0 0 90 0 0 0 0 0 0\ncpu0 5 0 0 45 0 0 0 0 0 0\ncpu1 5 0 0 45 0 0 0 0 0 0\nintr 123456\nctxt 98765\n");
    Assert(before.Idle == 90UL, "The idle component must come from the idle field.");
    Assert(before.Total == 100UL, "The total must sum every time field.");
    Assert(before.LogicalProcessorCount == 2, "Per-core cpu lines must be counted as logical CPUs.");

    // Idle delta 64 of total delta 128: 10+90=100 becomes 74+154=228.
    ProcStatSample after = NodeMetricsParser.ParseProcStat("cpu  74 0 0 154 0 0 0 0 0 0\ncpu0 5 0 0 45 0 0 0 0 0 0\ncpu1 5 0 0 45 0 0 0 0 0 0\n");
    Assert(after.Total == 228UL && after.Idle == 154UL, "The after sample must be parsed completely.");
    AssertNear(NodeMetricsParser.ComputeCpuUsageRatio(before, after)!.Value, 0.5, 1e-9, "Idle delta 64 of total delta 128 must yield a 0.5 busy ratio.");

    // iowait is counted as busy: idle unchanged, total grows through iowait only.
    ProcStatSample iowaitBefore = NodeMetricsParser.ParseProcStat("cpu  0 0 0 100 0 0 0 0 0 0\n");
    ProcStatSample iowaitAfter = NodeMetricsParser.ParseProcStat("cpu  0 0 0 100 50 0 0 0 0 0\n");
    AssertNear(NodeMetricsParser.ComputeCpuUsageRatio(iowaitBefore, iowaitAfter)!.Value, 1.0, 1e-9, "A pure iowait increase must count as busy time.");

    Assert(NodeMetricsParser.ComputeCpuUsageRatio(before, before) is null, "Identical samples must not produce a fabricated ratio.");
    Assert(NodeMetricsParser.ComputeCpuUsageRatio(after, before) is null, "A shrinking total must be rejected.");
    ProcStatSample idleDrop = NodeMetricsParser.ParseProcStat("cpu  70 0 0 90 0 0 0 0 0 0\n");
    Assert(NodeMetricsParser.ComputeCpuUsageRatio(iowaitAfter, idleDrop) is null, "A shrinking idle counter must be rejected even when the total grows.");
    Assert(NodeMetricsParser.ParseProcStat("garbage without a cpu line").Total == 0UL, "Content without a cpu line must produce an empty sample.");
    Console.WriteLine("PASS metrics: /proc/stat parsing and busy-ratio differential");

    // /proc/meminfo: kB values converted to bytes, MemAvailable preferred over MemFree.
    ProcMemInfo memInfo = NodeMetricsParser.ParseMemInfo("MemTotal:       16384256 kB\nMemFree:         1000000 kB\nMemAvailable:    8000000 kB\nHugePages_Total:       0\n");
    Assert(memInfo.TotalBytes == 16384256UL * 1024, "MemTotal must be converted from kB to bytes.");
    Assert(memInfo.AvailableBytes == 8000000UL * 1024, "MemAvailable must be preferred for the available bytes.");
    ProcMemInfo memInfoLegacy = NodeMetricsParser.ParseMemInfo("MemTotal:       16384256 kB\nMemFree:         1000000 kB\n");
    Assert(memInfoLegacy.AvailableBytes == 1000000UL * 1024, "Without MemAvailable the parser must fall back to MemFree.");
    Assert(NodeMetricsParser.ParseMemInfo("not a meminfo file").TotalBytes is null, "Garbage input must yield null values instead of throwing.");
    Assert(NodeMetricsParser.ParseMemInfo("MemTotal: 12 MB\n").TotalBytes is null, "Unknown units must be rejected instead of misinterpreted.");
    Console.WriteLine("PASS metrics: /proc/meminfo parsing with MemAvailable fallback");

    // /proc/uptime.
    Assert(NodeMetricsParser.ParseUptime("12345.67 89000.12\n").UptimeSeconds == 12345UL, "Uptime must be the truncated first field.");
    Assert(NodeMetricsParser.ParseUptime("0.00 0.00").UptimeSeconds == 0UL, "A zero uptime must be preserved.");
    Assert(NodeMetricsParser.ParseUptime("garbage").UptimeSeconds is null, "Unparseable uptime must yield null.");
    Console.WriteLine("PASS metrics: /proc/uptime parsing");

    // virsh domstats output: two domains, missing fields stay unset, malformed values are ignored.
    const string domStats = """
        Domain: 'web-01'
          state.state=1 state.reason=1
          cpu.time=245068545324 cpu.user=2220000000 cpu.system=1200000000
          vcpu.current=2 vcpu.maximum=4 vcpu.0.time=123456
          balloon.current=2097152 balloon.maximum=4194304

        Domain: 'db-02'
          state.state=1 state.reason=1
          cpu.time=1000
          balloon.current=512000
        """;
    List<VirshDomainStats> domains = NodeMetricsParser.ParseDomainStats(domStats);
    Assert(domains.Count == 2, "Both domains must be parsed.");
    VirshDomainStats web = domains[0];
    Assert(web.Name == "web-01" && web.CpuTimeNanoseconds == 245068545324UL, "The first domain must keep its name and cpu.time.");
    Assert(web.VirtualCpuCurrent == 2U && web.VirtualCpuMaximum == 4U, "The vcpu counts must be parsed.");
    Assert(web.BalloonCurrentKiB == 2097152UL && web.BalloonMaximumKiB == 4194304UL, "The balloon values must be parsed.");
    Assert(web.State == 1UL, "The domain state must be parsed.");
    VirshDomainStats db = domains[1];
    Assert(db.Name == "db-02" && db.CpuTimeNanoseconds == 1000UL, "The second domain must keep its name and cpu.time.");
    Assert(db.VirtualCpuCurrent is null && db.VirtualCpuMaximum is null && db.BalloonMaximumKiB is null, "Fields the agent did not report must stay unset.");
    Assert(NodeMetricsParser.ParseDomainStats("no domains here").Count == 0, "Output without domains must produce an empty list.");
    Assert(NodeMetricsParser.ParseDomainStats("Domain: 'broken'\n  cpu.time=not-a-number\n")[0].CpuTimeNanoseconds is null, "Malformed values must stay unset.");
    Console.WriteLine("PASS metrics: domstats output parsing");

    // Cumulative cpu.time differential: exact ratios, resets and skew clamping.
    AssertNear(NodeMetricsParser.ComputeCpuTimeRatio(1_000_000_000UL, 1_500_000_000UL, TimeSpan.FromSeconds(1), 1)!.Value, 0.5, 1e-9, "500ms of CPU time in one second on one CPU must be 0.5.");
    AssertNear(NodeMetricsParser.ComputeCpuTimeRatio(1_000_000_000UL, 1_500_000_000UL, TimeSpan.FromSeconds(1), 2)!.Value, 0.25, 1e-9, "The ratio must be normalized by the host logical CPU count.");
    Assert(NodeMetricsParser.ComputeCpuTimeRatio(1_500_000_000UL, 1_000_000_000UL, TimeSpan.FromSeconds(1), 1) is null, "A cpu.time counter reset must yield no value.");
    Assert(NodeMetricsParser.ComputeCpuTimeRatio(0, 0, TimeSpan.Zero, 1) is null, "A zero elapsed window must yield no value.");
    Assert(NodeMetricsParser.ComputeCpuTimeRatio(0, 0, TimeSpan.FromSeconds(-1), 1) is null, "A negative elapsed window must yield no value.");
    Assert(NodeMetricsParser.ComputeCpuTimeRatio(0, 0, TimeSpan.FromSeconds(1), 0) is null, "An unknown host CPU count must yield no value.");
    AssertNear(NodeMetricsParser.ComputeCpuTimeRatio(0, 2_000_000_000UL, TimeSpan.FromSeconds(1), 1)!.Value, 1.0, 1e-9, "Ratios above one (measurement skew) must clamp to 1.0.");
    Console.WriteLine("PASS metrics: cumulative cpu.time ratio differential");

    // Drive selection for the virtual machine storage directory.
    string[] linuxDrives = ["/", "/var", "/var/lib"];
    Assert(NodeMetricsParser.SelectDriveNameForDirectory("/var/lib/kvmcontrol/virtual-machines/web-01", linuxDrives) == "/var/lib", "The longest matching mount point must win.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory("/var/tmp", linuxDrives) == "/var", "A directory outside a deeper mount must fall back to the shorter mount.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory("/home/x", ["/", "/var"]) == "/", "Unmatched directories must fall back to the root drive.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory("/", linuxDrives) == "/", "The root directory must select the root drive.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory("/varlib/x", ["/var"]) is null, "A prefix must end on a path boundary so /var never matches /varlib.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory(@"C:\data\vms", ["C:\\"]) == "C:\\", "Windows drive roots must match their paths.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory(@"c:\data\vms", ["C:\\"]) == "C:\\", "Windows drive matching must be case-insensitive.");
    Assert(NodeMetricsParser.SelectDriveNameForDirectory("/var/lib", []) is null, "Without drives there is no selection.");
    Console.WriteLine("PASS metrics: storage directory drive selection");

    // Online/offline evaluation: fresh lastSeen is online, beyond N cycles is offline.
    DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
    TimeSpan interval = TimeSpan.FromSeconds(15);
    Assert(MetricsOnlineEvaluator.IsOnline(nowUtc, nowUtc, interval, 3), "A fresh lastSeen must be online.");
    Assert(MetricsOnlineEvaluator.IsOnline(nowUtc - interval * 3, nowUtc, interval, 3), "Exactly N missed cycles must still be online.");
    Assert(!MetricsOnlineEvaluator.IsOnline(nowUtc - interval * 3 - TimeSpan.FromTicks(1), nowUtc, interval, 3), "Beyond N missed cycles must be offline.");
    Assert(!MetricsOnlineEvaluator.IsOnline(null, nowUtc, interval, 3), "A node without any successful fetch must be offline.");
    Assert(!MetricsOnlineEvaluator.IsOnline(nowUtc, nowUtc, TimeSpan.Zero, 3), "An invalid interval must fail closed.");
    Assert(!MetricsOnlineEvaluator.IsOnline(nowUtc, nowUtc, interval, 0), "An invalid threshold must fail closed.");
    Console.WriteLine("PASS metrics: online/offline evaluation");

    // Last-good store: concurrent success/failure writers keep entry invariants.
    MetricsStore store = new();
    await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
    {
        for (int iteration = 0; iteration < 500; iteration++)
        {
            string node = $"node-{(worker + iteration) % 3}";
            store.RecordSuccess(node, new NodeMetricsReply { Host = new HostMetrics { LogicalCpuCount = (uint)iteration } }, DateTimeOffset.UtcNow);
            store.RecordFailure("node-x", "boom");
            if (store.TryGet(node) is not { } entry)
            {
                throw new InvalidOperationException("A recorded node must be readable.");
            }
            Assert((entry.Snapshot is null) == (entry.LastSeenUtc is null), "Snapshot and lastSeen must be set or unset together.");
            Assert(entry.Snapshot is null || entry.LastError is null, "A snapshot entry must not carry a stale error after success.");
            if (store.TryGet("node-x") is not { } failed)
            {
                throw new InvalidOperationException("A failed node must be readable.");
            }
            Assert(failed.LastSeenUtc is null && failed.Snapshot is null && failed.LastError is not null, "A node without any success must stay offline with an error.");
        }
    })));
    store.RecordSuccess("node-0", new NodeMetricsReply(), DateTimeOffset.UtcNow);
    store.RecordFailure("node-0", "later failure");
    NodeMetricsEntry lastGood = store.TryGet("node-0")!;
    Assert(lastGood.Snapshot is not null && lastGood.LastSeenUtc is not null, "A failure must keep the last good snapshot and lastSeen.");
    Assert(lastGood.LastError == "later failure", "A failure must update the error while keeping the snapshot.");
    Assert(store.TryGet("unknown") is null, "Unknown nodes must return null.");
    Console.WriteLine("PASS metrics: last-good store thread safety and invariants");

    // Aggregate summaries: sums over present fields only, null when nothing carries the field.
    ClusterVirtualMachineSummaryResponse empty = ClusterMetricsAggregator.SummarizeVirtualMachines([]);
    Assert(empty.Count == 0 && empty.AllocatedVirtualCpuCount is null && empty.AllocatedMemoryMiB is null, "An empty snapshot must summarize to zero without totals.");
    ClusterVirtualMachineSummaryResponse totals = ClusterMetricsAggregator.SummarizeVirtualMachines(
    [
        new VirtualMachineMetrics { Name = "a", AllocatedVirtualCpuCount = 4, AllocatedMemoryBytes = 2UL * 1024 * 1024 * 1024 },
        new VirtualMachineMetrics { Name = "b", AllocatedVirtualCpuCount = 2, AllocatedMemoryBytes = 1536UL * 1024 * 1024 }
    ]);
    Assert(totals.Count == 2 && totals.AllocatedVirtualCpuCount == 6UL && totals.AllocatedMemoryMiB == 3584UL, "Totals must sum allocated vCPU and memory across virtual machines.");
    ClusterVirtualMachineSummaryResponse partial = ClusterMetricsAggregator.SummarizeVirtualMachines(
    [
        new VirtualMachineMetrics { Name = "a", AllocatedVirtualCpuCount = 4 },
        new VirtualMachineMetrics { Name = "b" }
    ]);
    Assert(partial.Count == 2 && partial.AllocatedVirtualCpuCount == 4UL && partial.AllocatedMemoryMiB is null, "Only fields carried by at least one entry contribute to totals.");
    Console.WriteLine("PASS metrics: virtual machine summary aggregation");
}