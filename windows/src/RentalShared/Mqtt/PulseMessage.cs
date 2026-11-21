using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// ESP32 → server on `rental/coinacceptor/{acceptorId}/pulse`. Transient, QoS 1, has
// a monotonic `seqNo` per acceptor for application-level dedup. One message per
// completed coin burst (not per electrical pulse). In production peso-count mode,
// `pulseCount` and `denomination` are equal: ₱1=1, ₱5=5, ₱10=10, ₱20=20.
// Fast ₱1 stacks may merge into one burst, but the credited peso total remains correct.
public sealed record PulseMessage
{
    [JsonPropertyName("seqNo")]
    public long SeqNo { get; init; }

    [JsonPropertyName("denomination")]
    public int Denomination { get; init; }

    [JsonPropertyName("pulseCount")]
    public int PulseCount { get; init; }

    [JsonPropertyName("millisSinceBoot")]
    public long MillisSinceBoot { get; init; }
}
