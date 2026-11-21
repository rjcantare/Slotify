using System.Globalization;
using RentalServer.Core.Credits;
using RentalServer.Core.Stations;

namespace RentalServer.Core.Tests.Credits;

// Repository-level tests for the S26 credit-ack retry queue. Integration with
// SessionService.RetryStalePendingCreditsAsync is covered in
// SessionServiceTests.
public sealed class PendingCreditsRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private PendingCreditsRepository _repository = null!;
    private StationRepository _stationRepository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-pendingcredits-{Guid.NewGuid():N}.db");
        _stationRepository = new StationRepository(_databasePath);
        _repository = new PendingCreditsRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _repository.InitializeAsync();

        // FK on station_id requires the row to exist before we can insert.
        await _stationRepository.UpsertOnlineAsync(
            "pc-01",
            "pc",
            appVersion: "1.0.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));
        await _stationRepository.UpsertOnlineAsync(
            "tab-01",
            "tab",
            appVersion: "0.1.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Insert_then_delete_clears_the_row()
    {
        await _repository.InsertAsync("pc-01", 1, "{\"seqNo\":1}", FormatUtc(DateTimeOffset.UtcNow));
        Assert.Equal(1, await _repository.CountAsync());

        var deleted = await _repository.DeleteAsync("pc-01", 1);
        Assert.Equal(1, deleted);
        Assert.Equal(0, await _repository.CountAsync());
    }

    [Fact]
    public async Task Insert_is_idempotent_on_conflict()
    {
        var payload = "{\"seqNo\":7}";
        var issued = FormatUtc(DateTimeOffset.UtcNow);

        await _repository.InsertAsync("pc-01", 7, payload, issued);
        await _repository.InsertAsync("pc-01", 7, payload, issued); // duplicate

        Assert.Equal(1, await _repository.CountAsync());
    }

    [Fact]
    public async Task Delete_returns_zero_when_seqno_already_cleared()
    {
        var deleted = await _repository.DeleteAsync("pc-01", 99);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task ListStaleBefore_returns_rows_older_than_threshold_only()
    {
        var now = DateTimeOffset.UtcNow;
        var sixSecondsAgo = now.AddSeconds(-6);
        var oneSecondAgo = now.AddSeconds(-1);

        await _repository.InsertAsync("pc-01", 1, "{\"seqNo\":1}", FormatUtc(sixSecondsAgo));
        await _repository.InsertAsync("pc-01", 2, "{\"seqNo\":2}", FormatUtc(oneSecondAgo));

        var threshold = FormatUtc(now.AddSeconds(-5));
        var stale = await _repository.ListStaleBeforeAsync(threshold);

        Assert.Single(stale);
        Assert.Equal(1, stale[0].SeqNo);
    }

    [Fact]
    public async Task ListStaleBefore_considers_last_retry_at_when_set()
    {
        var now = DateTimeOffset.UtcNow;
        var sixSecondsAgo = now.AddSeconds(-6);
        var oneSecondAgo = now.AddSeconds(-1);

        // Row issued 6 s ago but already retried 1 s ago: should NOT be stale
        // until the next 5 s window elapses past the retry.
        await _repository.InsertAsync("pc-01", 1, "{\"seqNo\":1}", FormatUtc(sixSecondsAgo));
        await _repository.MarkRetriedAsync("pc-01", 1, FormatUtc(oneSecondAgo));

        var threshold = FormatUtc(now.AddSeconds(-5));
        var stale = await _repository.ListStaleBeforeAsync(threshold);

        Assert.Empty(stale);
    }

    [Fact]
    public async Task ListStaleBefore_orders_by_station_then_seqno()
    {
        var stamp = FormatUtc(DateTimeOffset.UtcNow.AddMinutes(-1));

        await _repository.InsertAsync("tab-01", 5, "{\"seqNo\":5}", stamp);
        await _repository.InsertAsync("pc-01", 7, "{\"seqNo\":7}", stamp);
        await _repository.InsertAsync("pc-01", 3, "{\"seqNo\":3}", stamp);

        var stale = await _repository.ListStaleBeforeAsync(FormatUtc(DateTimeOffset.UtcNow));

        // Stable monotonic order so the retry sweep republishes in seqno order
        // within each station, matching the original publish order.
        Assert.Equal(3, stale.Count);
        Assert.Equal(("pc-01", 3L), (stale[0].StationId, stale[0].SeqNo));
        Assert.Equal(("pc-01", 7L), (stale[1].StationId, stale[1].SeqNo));
        Assert.Equal(("tab-01", 5L), (stale[2].StationId, stale[2].SeqNo));
    }

    [Fact]
    public async Task MarkRetried_persists_last_retry_at_for_subsequent_sweeps()
    {
        var issued = FormatUtc(DateTimeOffset.UtcNow.AddMinutes(-1));
        await _repository.InsertAsync("pc-01", 1, "{\"seqNo\":1}", issued);

        var retryStamp = FormatUtc(DateTimeOffset.UtcNow);
        await _repository.MarkRetriedAsync("pc-01", 1, retryStamp);

        var rows = await _repository.ListStaleBeforeAsync(FormatUtc(DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Single(rows);
        Assert.Equal(retryStamp, rows[0].LastRetryAt);
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
