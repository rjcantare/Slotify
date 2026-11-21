using Microsoft.Data.Sqlite;

namespace RentalServer.Core.Persistence;

public static class RentalDatabase
{
    private const string ProgramDataFolderName = "RentalSystem";
    private const string DatabaseFileName = "rental.db";
    private const string BackupsFolderName = "backups";

    public static string GetDefaultDatabasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RentalSystem.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return Path.Combine(directory.FullName, "data", DatabaseFileName);
            }

            directory = directory.Parent;
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            return Path.Combine(programData, ProgramDataFolderName, DatabaseFileName);
        }

        return Path.Combine(Environment.CurrentDirectory, "data", DatabaseFileName);
    }

    public static string GetDefaultBackupRootPath()
    {
        var databasePath = GetDefaultDatabasePath();
        var databaseDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            return Path.Combine(databaseDirectory, BackupsFolderName);
        }

        return Path.Combine(Environment.CurrentDirectory, "data", BackupsFolderName);
    }

    public static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await journalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var foreignKeysCommand = connection.CreateCommand())
        {
            foreignKeysCommand.CommandText = "PRAGMA foreign_keys = ON;";
            await foreignKeysCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return connection;
    }
}
