namespace RentalServer.Core.Stations;

public sealed record StationRecord(
    string StationId,
    string DeviceType,
    string? DisplayName,
    string? LastSeenAt,
    string? AppVersion,
    bool IsOnline);
