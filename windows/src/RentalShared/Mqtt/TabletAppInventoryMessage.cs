using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

public sealed record TabletAppInventoryMessage
{
    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; init; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; init; } = string.Empty;

    [JsonPropertyName("apps")]
    public IReadOnlyList<TabletInstalledAppMessage> Apps { get; init; } =
        Array.Empty<TabletInstalledAppMessage>();
}

public sealed record TabletInstalledAppMessage
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("packageName")]
    public string PackageName { get; init; } = string.Empty;

    [JsonPropertyName("system")]
    public bool System { get; init; }
}
