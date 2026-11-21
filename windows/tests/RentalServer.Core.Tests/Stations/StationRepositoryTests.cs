using System.Globalization;
using RentalServer.Core.Stations;

namespace RentalServer.Core.Tests.Stations;

public sealed class StationRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private StationRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-stations-{Guid.NewGuid():N}.db");
        _repository = new StationRepository(_databasePath);

        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HideOfflineAsync_removes_offline_station_from_grid_snapshot()
    {
        await _repository.UpsertOnlineAsync(
            "pcvm-01",
            "pc",
            appVersion: "1.0.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));
        await _repository.MarkOfflineAsync("pcvm-01", FormatUtc(DateTimeOffset.UtcNow));

        var hidden = await _repository.HideOfflineAsync("pcvm-01");

        Assert.Equal(1, hidden);
        Assert.DoesNotContain(await _repository.GetAllAsync(), station => station.StationId == "pcvm-01");
        Assert.Null(await _repository.GetAsync("pcvm-01"));
    }

    [Fact]
    public async Task UpsertOnlineAsync_unhides_station_when_real_client_returns()
    {
        await _repository.UpsertOnlineAsync(
            "pc-02",
            "pc",
            appVersion: "1.0.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));
        await _repository.MarkOfflineAsync("pc-02", FormatUtc(DateTimeOffset.UtcNow));
        await _repository.HideOfflineAsync("pc-02");

        await _repository.UpsertOnlineAsync(
            "pc-02",
            "pc",
            appVersion: "1.0.1",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));

        var station = await _repository.GetAsync("pc-02");
        Assert.NotNull(station);
        Assert.True(station.IsOnline);
        Assert.Equal("1.0.1", station.AppVersion);
    }

    [Fact]
    public async Task HideOfflineAsync_does_not_hide_online_station()
    {
        await _repository.UpsertOnlineAsync(
            "pc-03",
            "pc",
            appVersion: "1.0.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));

        var hidden = await _repository.HideOfflineAsync("pc-03");

        Assert.Equal(0, hidden);
        Assert.NotNull(await _repository.GetAsync("pc-03"));
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
