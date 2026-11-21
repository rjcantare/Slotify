using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Sent by a station to `rental/station/{stationId}/claim/request` when the user taps
// Insert Coin. Transient, QoS 1, not retained.
public sealed record ClaimRequestMessage
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    [JsonPropertyName("requestedAt")]
    public string RequestedAt { get; init; } = string.Empty;
}
