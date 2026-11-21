namespace RentalServer.Core.Backups;

public sealed record BackupValidationResult(
    bool IsValid,
    string IntegrityCheck,
    int TableCount);
