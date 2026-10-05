using System.Globalization;
using System.Xml.Linq;

namespace Cluster.Services;

/// <summary>virsh dumpxml 输出的纯解析函数，不依赖 virsh 进程，便于跨平台回归测试。</summary>
public static class VirshDomainXmlParser
{
    public static VirshDomainDefinition Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The domain XML could not be parsed.", exception);
        }

        XElement? root = document.Root;
        if (root is null || root.Name.LocalName != "domain")
        {
            throw new InvalidOperationException("The domain XML does not contain a domain element.");
        }

        string uuid = root.Element("uuid")?.Value.Trim()
            ?? throw new InvalidOperationException("The domain XML does not contain a UUID.");
        ulong maximumMemoryKiB = ParseMemory(root.Element("memory"));
        ulong currentMemoryKiB = root.Element("currentMemory") is null ? maximumMemoryKiB : ParseMemory(root.Element("currentMemory"));
        uint virtualCpuCount = uint.TryParse(root.Element("vcpu")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsedVcpu)
            ? parsedVcpu
            : throw new InvalidOperationException("The domain XML does not contain a valid vCPU count.");

        List<VirshDomainDisk> disks = new();
        foreach (XElement disk in root.Elements("devices").Elements("disk"))
        {
            disks.Add(new VirshDomainDisk(
                disk.Attribute("device")?.Value ?? "disk",
                disk.Element("target")?.Attribute("dev")?.Value ?? string.Empty,
                disk.Element("source")?.Attribute("file")?.Value,
                disk.Element("driver")?.Attribute("type")?.Value));
        }

        return new VirshDomainDefinition(uuid, maximumMemoryKiB, currentMemoryKiB, virtualCpuCount, disks);
    }

    /// <summary>libvirt 省略 unit 属性时默认单位为 KiB。</summary>
    private static ulong ParseMemory(XElement? element)
    {
        if (element is null || !ulong.TryParse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
        {
            throw new InvalidOperationException("The domain XML does not contain a valid memory amount.");
        }

        return element.Attribute("unit")?.Value.Trim().ToLowerInvariant() switch
        {
            null or "" or "kib" or "k" => value,
            "mib" or "m" => checked(value * 1024),
            "gib" or "g" => checked(value * 1024 * 1024),
            "tib" or "t" => checked(value * 1024 * 1024 * 1024),
            "b" or "bytes" => checked((value + 1023) / 1024),
            _ => throw new InvalidOperationException("The domain XML memory unit is not supported.")
        };
    }
}
