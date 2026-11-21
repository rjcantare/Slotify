namespace RentalServer.Core.Backups;

public sealed record BackupIncludedFile(
    string SourcePath,
    string BackupRelativePath,
    long SizeBytes,
    string Sha256);
