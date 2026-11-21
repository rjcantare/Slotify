using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Wire shape of the retained `rental/station/{stationId}/session` payload.
// Stations treat this as truth on reconnect and reconcile locally between updates;
// the server only publishes on state transitions (start, credit, lock, end).
public sealed record SessionMessage
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; init; } = string.Empty;

    [JsonPropertyName("locked")]
    public bool Locked { get; init; }

    [JsonPropertyName("remainingSec")]
    public int RemainingSec { get; init; }

    [JsonPropertyName("lastCreditSeqNo")]
    public long LastCreditSeqNo { get; init; }

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;
}
