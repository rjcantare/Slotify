using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Sent by a station to `rental/station/{stationId}/credit/ack` in response to a
// credit message. Transient, QoS 1, not retained. Per protocol the station ACKs
// even on duplicate so the server's retry path (if any) stops.
public sealed record CreditAckMessage
{
    [JsonPropertyName("seqNo")]
    public long SeqNo { get; init; }

    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;
}
