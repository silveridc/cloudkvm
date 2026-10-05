namespace Cluster.Services;

/// <summary>虚拟机不存在。</summary>
public sealed class VirtualMachineNotFoundException(string name) : Exception($"Virtual machine '{name}' was not found.")
{
    public string Name { get; } = name;
}
