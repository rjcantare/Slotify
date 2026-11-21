namespace RentalServer.Core.TabletApps;

public sealed record TabletAppDefinition(
    string DisplayName,
    string PackageName,
    bool Enabled,
    bool DeepFreeze,
    int SortOrder,
    string Category = TabletAppCategories.Other);
