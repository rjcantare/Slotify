using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RentalServer.Core.Backups;

public sealed class ServerBackupService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<ServerBackupResult> CreateBackupAsync(
        ServerBackupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BackupRootPath);

        if (options.RetentionCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Backup retention count must be at least 1.");
        }

        if (!File.Exists(options.DatabasePath))
        {
            throw new FileNotFoundException("Rental database was not found.", options.DatabasePath);
        }

        Directory.CreateDirectory(options.BackupRootPath);

        var createdAt = DateTimeOffset.UtcNow;
        var backupDirectory = CreateBackupDirectory(options.BackupRootPath, createdAt);
        var databaseBackupPath = Path.Combine(backupDirectory, "rental.db");

        await BackupSqliteDatabaseAsync(options.DatabasePath, databaseBackupPath, cancellationToken);

        var includedFiles = await CopyIncludedFilesAsync(
            options,
            backupDirectory,
            cancellationToken);

        var validation = await ValidateBackupAsync(databaseBackupPath, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Backup validation failed: integrity_check returned '{validation.IntegrityCheck}'.");
        }

        var manifest = new BackupManifest(
            SchemaVersion: 1,
            CreatedAtUtc: createdAt.ToString("O"),
            MachineName: Environment.MachineName,
            Reason: string.IsNullOrWhiteSpace(options.Reason) ? "manual" : options.Reason,
            SourceDatabasePath: Path.GetFullPath(options.DatabasePath),
            DatabaseFileName: Path.GetFileName(databaseBackupPath),
            DatabaseSizeBytes: new FileInfo(databaseBackupPath).Length,
            DatabaseSha256: await ComputeSha256Async(databaseBackupPath, cancellationToken),
            IncludedFiles: includedFiles,
            Validation: validation);

        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions),
            cancellationToken);

        var offMachineCopyDirectory = CopyToOffMachineTarget(
            backupDirectory,
            options.OffMachineCopyRootPath);

        ApplyRetention(options.BackupRootPath, options.RetentionCount);

        return new ServerBackupResult(
            BackupDirectory: backupDirectory,
            DatabaseBackupPath: databaseBackupPath,
            ManifestPath: manifestPath,
            Validation: validation,
            OffMachineCopyDirectory: offMachineCopyDirectory);
    }

    public async Task<BackupValidationResult> ValidateBackupAsync(
        string databaseBackupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseBackupPath);

        await using var connection = new SqliteConnection(CreateConnectionString(databaseBackupPath));
        await connection.OpenAsync(cancellationToken);

        var integrityCheck = "not-run";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            integrityCheck = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
        }

        var tableCount = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%';
                """;
            tableCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        return new BackupValidationResult(
            IsValid: string.Equals(integrityCheck, "ok", StringComparison.OrdinalIgnoreCase) && tableCount > 0,
            IntegrityCheck: integrityCheck,
            TableCount: tableCount);
    }

    private static async Task BackupSqliteDatabaseAsync(
        string sourceDatabasePath,
        string destinationDatabasePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDatabasePath)!);

        if (File.Exists(destinationDatabasePath))
        {
            File.Delete(destinationDatabasePath);
        }

        await using var source = new SqliteConnection(CreateConnectionString(sourceDatabasePath));
        await source.OpenAsync(cancellationToken);

        await using var destination = new SqliteConnection(CreateConnectionString(destinationDatabasePath));
        await destination.OpenAsync(cancellationToken);

        source.BackupDatabase(destination);
    }

    private static async Task<IReadOnlyList<BackupIncludedFile>> CopyIncludedFilesAsync(
        ServerBackupOptions options,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var copied = new List<BackupIncludedFile>();
        var filesDirectory = Path.Combine(backupDirectory, "files");

        foreach (var filePath in options.ConfigFilePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.Combine("config", Path.GetFileName(filePath));
            var destinationPath = Path.Combine(filesDirectory, relativePath);
            copied.Add(await CopyFileAsync(filePath, destinationPath, relativePath, cancellationToken));
        }

        foreach (var directoryPath in options.AssetDirectoryPaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativeFromAssetRoot = Path.GetRelativePath(directoryPath, filePath);
                var relativePath = Path.Combine(
                    "assets",
                    Path.GetFileName(directoryPath),
                    relativeFromAssetRoot);
                var destinationPath = Path.Combine(filesDirectory, relativePath);
                copied.Add(await CopyFileAsync(filePath, destinationPath, relativePath, cancellationToken));
            }
        }

        return copied;
    }

    private static async Task<BackupIncludedFile> CopyFileAsync(
        string sourcePath,
        string destinationPath,
        string backupRelativePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        await using (var source = File.OpenRead(sourcePath))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        return new BackupIncludedFile(
            SourcePath: Path.GetFullPath(sourcePath),
            BackupRelativePath: backupRelativePath.Replace('\\', '/'),
            SizeBytes: new FileInfo(destinationPath).Length,
            Sha256: await ComputeSha256Async(destinationPath, cancellationToken));
    }

    private static string? CopyToOffMachineTarget(
        string backupDirectory,
        string? offMachineCopyRootPath)
    {
        if (string.IsNullOrWhiteSpace(offMachineCopyRootPath))
        {
            return null;
        }

        Directory.CreateDirectory(offMachineCopyRootPath);

        var destinationDirectory = Path.Combine(
            offMachineCopyRootPath,
            Path.GetFileName(backupDirectory));

        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }

        CopyDirectory(backupDirectory, destinationDirectory);
        return destinationDirectory;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, destinationPath, overwrite: true);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(directoryPath));
            CopyDirectory(directoryPath, destinationPath);
        }
    }

    private static void ApplyRetention(string backupRootPath, int retentionCount)
    {
        var backupDirectories = Directory
            .EnumerateDirectories(backupRootPath, "backup-*", SearchOption.TopDirectoryOnly)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var directory in backupDirectories.Skip(retentionCount))
        {
            directory.Delete(recursive: true);
        }
    }

    private static string CreateBackupDirectory(string backupRootPath, DateTimeOffset createdAt)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
            var directory = Path.Combine(
                backupRootPath,
                $"backup-{createdAt:yyyyMMdd-HHmmss}-{Environment.MachineName}{suffix}");
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        var fallback = Path.Combine(
            backupRootPath,
            $"backup-{createdAt:yyyyMMdd-HHmmss}-{Environment.MachineName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        };
        return builder.ToString();
    }
}
