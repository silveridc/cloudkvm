namespace Control.Services;

public sealed class ControlInstance
{
    public string Id { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
