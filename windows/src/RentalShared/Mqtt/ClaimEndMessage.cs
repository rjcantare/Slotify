using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Sent by a station to `rental/station/{stationId}/claim/end` when the user clicks
// DONE inside the multi-coin window. Transient, QoS 1, not retained. `claimId` lets
// the server reject stale end messages from a prior claim.
public sealed record ClaimEndMessage
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("claimId")]
    public string ClaimId { get; init; } = string.Empty;

    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    [JsonPropertyName("endedAt")]
    public string EndedAt { get; init; } = string.Empty;
}
