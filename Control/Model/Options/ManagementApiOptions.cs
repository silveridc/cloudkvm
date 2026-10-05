namespace Control.Model.Options;

/// <summary>管理 API 认证令牌配置（ManagementApi 配置节）。</summary>
public sealed class ManagementApiOptions
{
    public const string SectionName = "ManagementApi";

    public string Token { get; set; } = string.Empty;
}
