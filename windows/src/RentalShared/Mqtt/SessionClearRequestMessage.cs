namespace RentalShared.Mqtt;

// Station -> server on `rental/station/{stationId}/session/clear`.
// The station requests a server-authoritative clear; server publishes the
// retained session update if accepted.
public sealed record SessionClearRequestMessage
{
    public string RequestId { get; init; } = string.Empty;

    public string StationId { get; init; } = string.Empty;

    public string RequestedAt { get; init; } = string.Empty;
}
