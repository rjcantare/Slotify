using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

/// <summary>
/// Server-originated admin command sent to a single station.
/// QoS 1, transient, on rental/station/{stationId}/command.
/// </summary>
public sealed record AdminCommandMessage
{
    /// <summary>Unique idempotency key — server-minted per command.</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>restart_client | reboot_pc | shutdown_pc</summary>
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("issuedAt")]
    public string IssuedAt { get; init; } = string.Empty;

    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable reason logged to the audit trail so later
    /// dispute-resolution queries can distinguish "nightly reboot"
    /// from "frozen station recovery."
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
