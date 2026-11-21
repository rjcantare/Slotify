using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Server → station on `rental/station/{stationId}/credit`. Transient (the retained
// `session` topic is the truth for `remainingSec`); this event mainly drives the
// "+30 minutes" UX toast on the client. `source`: "coin" | "admin" | "promo".
// `denomination` (peso value) present only when `source == "coin"`.
public sealed record CreditMessage
{
    [JsonPropertyName("seqNo")]
    public long SeqNo { get; init; }

    [JsonPropertyName("secondsToAdd")]
    public int SecondsToAdd { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("denomination")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Denomination { get; init; }

    [JsonPropertyName("issuedAt")]
    public string IssuedAt { get; init; } = string.Empty;
}
