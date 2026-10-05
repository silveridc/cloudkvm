namespace Cluster.Interface;

/// <summary>按虚拟机名称串行化操作的进程内锁。</summary>
public interface IVirtualMachineLockManager
{
    Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
    Task RunAsync(string name, Func<CancellationToken, Task> action, CancellationToken cancellationToken);
}
