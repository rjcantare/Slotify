using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Retained on `rental/station/{stationId}/status` and registered as the LWT.
// Online payload carries deviceType/appVersion/uptime; LWT payload is fixed at CONNECT
// and only carries `online: false` + `deviceId` (no timestamp — server stamps on receipt).
public sealed record StationStatusMessage
{
    [JsonPropertyName("online")]
    public bool Online { get; init; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("deviceType")]
    public string? DeviceType { get; init; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("uptimeSec")]
    public long? UptimeSec { get; init; }
}
