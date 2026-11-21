using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

/// <summary>
/// Client-originated acknowledgement of an admin command.
/// QoS 1, transient, on rental/station/{stationId}/command/ack.
/// </summary>
public sealed record AdminCommandAckMessage
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    /// <summary>acknowledged | rejected | completed</summary>
    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;
}
