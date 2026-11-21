using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Retained on `rental/coinacceptor/{acceptorId}/status` and registered as the LWT.
// Online payload carries firmware/uptime/rssi; LWT payload is fixed at CONNECT and
// only carries `online: false` + `deviceId` (no timestamp — server stamps on receipt).
public sealed record AcceptorStatusMessage
{
    [JsonPropertyName("online")]
    public bool Online { get; init; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("firmwareVersion")]
    public string? FirmwareVersion { get; init; }

    [JsonPropertyName("uptimeSec")]
    public long? UptimeSec { get; init; }

    [JsonPropertyName("rssi")]
    public int? Rssi { get; init; }
}
