using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Server → ESP32 on `rental/coinacceptor/{acceptorId}/led`. NOT retained — a retained
// "on" after a server crash would lie about an active claim that no longer exists.
// `autoOffSec` is the ESP32's safety timeout: if no `off` arrives by then, the ESP32
// turns the LED off itself. `claimId` lets the ESP32 ignore stale `off` from a prior
// claim if it arrives out of order.
public sealed record LedCommandMessage
{
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty; // "on" | "off"

    [JsonPropertyName("autoOffSec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AutoOffSec { get; init; }

    [JsonPropertyName("claimId")]
    public string ClaimId { get; init; } = string.Empty;
}
