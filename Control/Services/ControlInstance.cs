namespace Control.Services;

/// <summary>当前 Control 实例的唯一标识，用于操作归属与存活判断。</summary>
public sealed class ControlInstance
{
    public string Id { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
