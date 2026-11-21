using Microsoft.Data.Sqlite;
using RentalServer.Core.Backups;
using RentalServer.Core.Rates;
using RentalServer.Core.Stations;

namespace RentalServer.Core.Tests.Backups;

public sealed class ServerBackupServiceTests : IAsyncLifetime
{
    private readonly ServerBackupService _service = new();
    private string _root = string.Empty;
    private string _databasePath = string.Empty;
    private string _backupRoot = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rental-test-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _databasePath = Path.Combine(_root, "rental.db");
        _backupRoot = Path.Combine(_root, "backups");

        var stationRepository = new StationRepository(_databasePath);
        var rateRepository = new RateRepository(_databasePath);

        await stationRepository.InitializeAsync();
        await rateRepository.InitializeAsync();
        await rateRepository.EnsureSeededAsync("2026-05-27T00:00:00.000Z");
        await stationRepository.UpsertOnlineAsync(
            "tab-01",
            "tab",
            "0.1.1",
            "2026-05-27T00:00:00.000Z");
    }

    public Task DisposeAsync()
    {
        TryDeleteDirectory(_root);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateBackupAsync_uses_sqlite_backup_api_and_validates_copy()
    {
        var result = await _service.CreateBackupAsync(new ServerBackupOptions(
            DatabasePath: _databasePath,
            BackupRootPath: _backupRoot));

        Assert.True(File.Exists(result.DatabaseBackupPath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(result.Validation.IsValid);
        Assert.Equal("ok", result.Validation.IntegrityCheck);

        await using var connection = new SqliteConnection($"Data Source={result.DatabaseBackupPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_type FROM stations WHERE station_id = 'tab-01';";

        Assert.Equal("tab", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CreateBackupAsync_copies_config_files_and_off_machine_target()
    {
        var configPath = Path.Combine(_root, "appsettings.Local.json");
        await File.WriteAllTextAsync(configPath, """{"broker":"127.0.0.1"}""");
        var offMachineRoot = Path.Combine(_root, "usb-copy");

        var result = await _service.CreateBackupAsync(new ServerBackupOptions(
            DatabasePath: _databasePath,
            BackupRootPath: _backupRoot)
        {
            ConfigFilePaths = new[] { configPath },
            OffMachineCopyRootPath = offMachineRoot
        });

        var copiedConfig = Path.Combine(result.BackupDirectory, "files", "config", "appsettings.Local.json");
        Assert.True(File.Exists(copiedConfig));
        Assert.NotNull(result.OffMachineCopyDirectory);
        Assert.True(File.Exists(Path.Combine(result.OffMachineCopyDirectory!, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.OffMachineCopyDirectory!, "rental.db")));
    }

    [Fact]
    public async Task CreateBackupAsync_applies_retention_to_oldest_backups()
    {
        for (var i = 0; i < 3; i++)
        {
            await _service.CreateBackupAsync(new ServerBackupOptions(
                DatabasePath: _databasePath,
                BackupRootPath: _backupRoot)
            {
                RetentionCount = 2
            });
        }

        var backupDirectories = Directory.GetDirectories(_backupRoot, "backup-*");
        Assert.Equal(2, backupDirectories.Length);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
