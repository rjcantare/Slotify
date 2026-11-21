using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Server → ESP32 on `rental/coinacceptor/{acceptorId}/pulse/ack`. Tells the ESP32 it
// can remove that seqNo from its NVS replay buffer. Published EVEN for duplicate /
// stale seqNos — otherwise the ESP32 retries forever.
public sealed record PulseAckMessage
{
    [JsonPropertyName("seqNo")]
    public long SeqNo { get; init; }
}
