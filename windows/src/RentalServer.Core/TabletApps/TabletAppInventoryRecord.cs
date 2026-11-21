namespace RentalServer.Core.TabletApps;

public sealed record TabletAppInventoryRecord(
    string StationId,
    string DeviceType,
    string PublishedAt,
    IReadOnlyList<TabletInstalledAppRecord> Apps);

public sealed record TabletInstalledAppRecord(
    string DisplayName,
    string PackageName,
    bool System);
