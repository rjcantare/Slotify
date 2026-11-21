using System.Text.Json.Serialization;

namespace RentalShared.Updates;

/// <summary>
/// Retained MQTT manifest published by the operator when a new release is
/// ready. Clients (Windows + Android) subscribe and compare against their
/// own appVersion. The server checks its own server.version against the
/// running assembly version.
/// </summary>
public sealed record UpdateManifest
{
    public const string MqttTopic = "rental/config/update";

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; init; } = string.Empty;

    [JsonPropertyName("server")]
    public UpdateComponent? Server { get; init; }

    [JsonPropertyName("client")]
    public UpdateComponent? Client { get; init; }

    [JsonPropertyName("android")]
    public UpdateComponent? Android { get; init; }
}

public sealed record UpdateComponent
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }
}
