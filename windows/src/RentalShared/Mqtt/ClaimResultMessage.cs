using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Server reply on `rental/station/{stationId}/claim/result`. Transient, QoS 1.
// Granted/denied shape per the locked protocol — optional fields only populated for
// the relevant branch. `reason` enum: "slot_in_use" | "no_acceptor_online" |
// "station_unknown" | "abuse_ban". `heldBy` populated only when reason == "slot_in_use".
// `banUntil` populated only when reason == "abuse_ban" (ISO 8601 UTC).
public sealed record ClaimResultMessage
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty; // "granted" | "denied"

    [JsonPropertyName("claimId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimId { get; init; }

    [JsonPropertyName("windowSec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowSec { get; init; }

    [JsonPropertyName("acceptorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcceptorId { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("heldBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeldBy { get; init; }

    [JsonPropertyName("banUntil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BanUntil { get; init; }
}
