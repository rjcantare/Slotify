namespace RentalServer.Core.Backups;

public sealed record ServerBackupResult(
    string BackupDirectory,
    string DatabaseBackupPath,
    string ManifestPath,
    BackupValidationResult Validation,
    string? OffMachineCopyDirectory);
