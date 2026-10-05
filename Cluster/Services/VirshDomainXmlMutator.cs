using System.Globalization;
using System.Xml.Linq;

namespace Cluster.Services;

/// <summary>对 dumpxml 输出做纯变换：只改 memory/currentMemory/vcpu 后重新 define，避免 undefine 窗口。</summary>
public static class VirshDomainXmlMutator
{
    public static string WithCpuAndMemory(string xml, uint virtualCpuCount, ulong memoryKiB)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        XElement? root = document.Root;
        if (root is null || root.Name.LocalName != "domain")
        {
            throw new InvalidOperationException("The domain XML does not contain a domain element.");
        }

        string memoryKiBText = memoryKiB.ToString(CultureInfo.InvariantCulture);
        XElement memory = root.Element("memory") ?? throw new InvalidOperationException("The domain XML does not contain a memory element.");
        SetKiBValue(memory, memoryKiBText);
        XElement? currentMemory = root.Element("currentMemory");
        if (currentMemory is null)
        {
            currentMemory = new XElement("currentMemory");
            memory.AddAfterSelf(currentMemory);
        }
        SetKiBValue(currentMemory, memoryKiBText);

        XElement vcpu = root.Element("vcpu") ?? throw new InvalidOperationException("The domain XML does not contain a vcpu element.");
        vcpu.Value = virtualCpuCount.ToString(CultureInfo.InvariantCulture);
        vcpu.Attribute("current")?.Remove();

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void SetKiBValue(XElement element, string memoryKiBText)
    {
        element.SetValue(memoryKiBText);
        element.Attribute("unit")?.Remove();
    }
}
