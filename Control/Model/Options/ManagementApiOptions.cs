namespace Control.Model.Options;

public sealed class ManagementApiOptions
{
    public const string SectionName = "ManagementApi";

    public string Token { get; set; } = string.Empty;
}
