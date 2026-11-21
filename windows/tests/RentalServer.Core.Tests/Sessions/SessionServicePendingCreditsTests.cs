using System.Globalization;
using RentalServer.Core.Credits;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;

namespace RentalServer.Core.Tests.Sessions;

// S26 integration: ApplyCredit drops a row into pending_credits, the retry
// sweep republishes anything older than the 5-second ACK window, and a
// successful ACK (modelled by direct repository delete) clears the queue.
public sealed class SessionServicePendingCreditsTests : IAsyncLifetime
{
    private const string StationId = "tab-01";
    private const string DeviceType = "tab";

    private string _databasePath = string.Empty;
    private StationRepository _stationRepository = null!;
    private SessionRepository _sessionRepository = null!;
    private PendingCreditsRepository _pendingCredits = null!;
    private FakeMonotonicClock _clock = null!;
    private FakeSessionPublisher _sessionPublisher = null!;
    private FakeCreditPublisher _creditPublisher = null!;
    private SessionService _service = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-pcr-{Guid.NewGuid():N}.db");
        _stationRepository = new StationRepository(_databasePath);
        _sessionRepository = new SessionRepository(_databasePath);
        _pendingCredits = new PendingCreditsRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _sessionRepository.InitializeAsync();
        await _pendingCredits.InitializeAsync();
        await _stationRepository.UpsertOnlineAsync(
            StationId,
            DeviceType,
            appVersion: "0.1.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));

        _clock = new FakeMonotonicClock();
        _sessionPublisher = new FakeSessionPublisher();
        _creditPublisher = new FakeCreditPublisher();
        _service = new SessionService(
            _sessionRepository,
            _sessionPublisher,
            _creditPublisher,
            _clock,
            eventLogger: null,
            pendingCredits: _pendingCredits);
        await _service.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Apply_credit_inserts_pending_row_with_serialized_payload()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 600, source: "coin", denomination: 10);

        Assert.Equal(1, await _pendingCredits.CountAsync());

        // The serialized payload must round-trip back to a CreditMessage with
        // the same seqNo so the sweep can republish it untouched.
        var rows = await _pendingCredits.ListStaleBeforeAsync(
            FormatUtc(DateTimeOffset.UtcNow.AddSeconds(60)));
        Assert.Single(rows);
        Assert.Equal(StationId, rows[0].StationId);
        Assert.Equal(1L, rows[0].SeqNo);
        Assert.Contains("\"seqNo\":1", rows[0].Payload);
        Assert.Contains("\"secondsToAdd\":600", rows[0].Payload);
    }

    [Fact]
    public async Task Retry_sweep_republishes_stale_credits_and_marks_them_retried()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, 600, source: "coin", denomination: 10);

        // Backdate issued_at so the row is older than the 5 s threshold without
        // having to actually wait on the wall clock.
        BackdateIssuedAt(StationId, 1, DateTimeOffset.UtcNow.AddSeconds(-10));

        var publishesBeforeSweep = _creditPublisher.PublishCount;
        await _service.RetryStalePendingCreditsAsync();

        Assert.Equal(publishesBeforeSweep + 1, _creditPublisher.PublishCount);
        Assert.Equal(1, _creditPublisher.LastMessage!.SeqNo);

        // The retry stamp now anchors freshness, so the next sweep should be a no-op.
        await _service.RetryStalePendingCreditsAsync();
        Assert.Equal(publishesBeforeSweep + 1, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task Sweep_is_a_noop_when_no_rows_are_stale()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, 600, source: "coin", denomination: 10);

        var publishesBefore = _creditPublisher.PublishCount;
        await _service.RetryStalePendingCreditsAsync();

        Assert.Equal(publishesBefore, _creditPublisher.PublishCount);
        Assert.Equal(1, await _pendingCredits.CountAsync());
    }

    [Fact]
    public async Task Delete_after_ack_prevents_further_retries()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, 600, source: "coin", denomination: 10);

        // Simulate the WPF host's credit/ack handler removing the row.
        var deleted = await _pendingCredits.DeleteAsync(StationId, 1);
        Assert.Equal(1, deleted);

        // Even past the 5 s window the sweep finds nothing to republish.
        BackdateIssuedAt(StationId, 1, DateTimeOffset.UtcNow.AddSeconds(-10));
        var publishesBefore = _creditPublisher.PublishCount;
        await _service.RetryStalePendingCreditsAsync();

        Assert.Equal(publishesBefore, _creditPublisher.PublishCount);
        Assert.Equal(0, await _pendingCredits.CountAsync());
    }

    [Fact]
    public async Task Tick_triggers_retry_sweep_on_existing_pending_rows()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, 600, source: "coin", denomination: 10);

        BackdateIssuedAt(StationId, 1, DateTimeOffset.UtcNow.AddSeconds(-10));
        var publishesBefore = _creditPublisher.PublishCount;

        await _service.TickAsync();

        Assert.Equal(publishesBefore + 1, _creditPublisher.PublishCount);
    }

    // The repository does not expose an InsertAsync with a custom issued_at
    // for the purposes of testing because production code always uses "now".
    // To exercise the stale path without a real-time wait, we open the SQLite
    // file and patch the row directly. This is test-only.
    private void BackdateIssuedAt(string stationId, long seqNo, DateTimeOffset issuedAt)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE pending_credits SET issued_at = $issued WHERE station_id = $station_id AND seqno = $seqno;";
        command.Parameters.AddWithValue("$issued", FormatUtc(issuedAt));
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$seqno", seqNo);
        command.ExecuteNonQuery();
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
