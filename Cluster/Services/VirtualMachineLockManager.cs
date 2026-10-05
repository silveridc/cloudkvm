using System.Security.Cryptography;
using System.Text;
using Cluster.Interface;

namespace Cluster.Services;

/// <summary>按名称串行化虚拟机操作：名称哈希到固定信号量槽位，同一台机器的创建/删除/改配/扩容互斥。</summary>
public sealed class VirtualMachineLockManager : IVirtualMachineLockManager
{
    private const int SlotCount = 256;
    private static readonly SemaphoreSlim[] _slots = Enumerable.Range(0, SlotCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        SemaphoreSlim slot = GetSlot(name);
        await slot.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            slot.Release();
        }
    }

    public async Task RunAsync(string name, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        SemaphoreSlim slot = GetSlot(name);
        await slot.WaitAsync(cancellationToken);
        try
        {
            await action(cancellationToken);
        }
        finally
        {
            slot.Release();
        }
    }

    private static SemaphoreSlim GetSlot(string name)
    {
        uint hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0);
        return _slots[hash % (uint)SlotCount];
    }
}
