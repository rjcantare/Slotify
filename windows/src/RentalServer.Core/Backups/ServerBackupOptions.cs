namespace RentalServer.Core.Backups;

public sealed record ServerBackupOptions(
    string DatabasePath,
    string BackupRootPath)
{
    public IReadOnlyList<string> ConfigFilePaths { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AssetDirectoryPaths { get; init; } = Array.Empty<string>();

    public string? OffMachineCopyRootPath { get; init; }

    public int RetentionCount { get; init; } = 14;

    public string Reason { get; init; } = "manual";
}
