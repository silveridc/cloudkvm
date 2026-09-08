using System.ComponentModel.DataAnnotations;

namespace Cluster.Models.Options;

public sealed class RpcOptions
{
    public const string SectionName = "Rpc";

    [Required]
    public string Token { get; init; } = string.Empty;
}
