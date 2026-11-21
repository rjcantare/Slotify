namespace RentalServer.Core.Backups;

public sealed record BackupManifest(
    int SchemaVersion,
    string CreatedAtUtc,
    string MachineName,
    string Reason,
    string SourceDatabasePath,
    string DatabaseFileName,
    long DatabaseSizeBytes,
    string DatabaseSha256,
    IReadOnlyList<BackupIncludedFile> IncludedFiles,
    BackupValidationResult Validation);
