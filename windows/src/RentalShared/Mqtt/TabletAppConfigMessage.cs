using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

public sealed record TabletAppConfigMessage
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;

    [JsonPropertyName("apps")]
    public IReadOnlyList<TabletAppConfigItemMessage> Apps { get; init; } =
        Array.Empty<TabletAppConfigItemMessage>();
}

public sealed record TabletAppConfigItemMessage
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("packageName")]
    public string PackageName { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("deepFreeze")]
    public bool DeepFreeze { get; init; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;
}
