using System.Globalization;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;

namespace RentalServer.Core.Tests.Sessions;

public sealed class SessionServiceTests : IAsyncLifetime
{
    private const string StationId = "pc-01";
    private const string DeviceType = "pc";

    private string _databasePath = string.Empty;
    private StationRepository _stationRepository = null!;
    private SessionRepository _sessionRepository = null!;
    private FakeMonotonicClock _clock = null!;
    private FakeSessionPublisher _sessionPublisher = null!;
    private FakeCreditPublisher _creditPublisher = null!;
    private SessionService _service = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");
        _stationRepository = new StationRepository(_databasePath);
        _sessionRepository = new SessionRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _sessionRepository.InitializeAsync();
        await _stationRepository.UpsertOnlineAsync(
            StationId,
            DeviceType,
            appVersion: "1.0.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));

        _clock = new FakeMonotonicClock();
        _sessionPublisher = new FakeSessionPublisher();
        _creditPublisher = new FakeCreditPublisher();
        _service = new SessionService(_sessionRepository, _sessionPublisher, _creditPublisher, _clock);
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
    public async Task StartSession_creates_locked_zero_session_and_publishes_once()
    {
        var state = await _service.StartSessionAsync(StationId, DeviceType);

        Assert.True(state.IsLocked);
        Assert.Equal(0, state.RemainingSecondsAtAnchor);
        Assert.Equal(0, state.LastCreditSeqNo);
        Assert.Equal(1, _sessionPublisher.PublishCount);
        Assert.Equal(0, _creditPublisher.PublishCount);
        Assert.Equal(StationId, _sessionPublisher.LastState!.StationId);
    }

    [Fact]
    public async Task StartSession_is_idempotent_for_same_station()
    {
        var first = await _service.StartSessionAsync(StationId, DeviceType);
        var second = await _service.StartSessionAsync(StationId, DeviceType);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(1, _sessionPublisher.PublishCount);
    }

    [Fact]
    public async Task ApplyCredit_raises_remaining_unlocks_and_publishes_credit_then_session()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        var after = await _service.ApplyCreditAsync(StationId, secondsToAdd: 1800, source: "coin", denomination: 20);

        Assert.False(after.IsLocked);
        Assert.Equal(1800, after.RemainingSecondsAtAnchor);
        Assert.Equal(1, after.LastCreditSeqNo);
        Assert.Equal(2, _sessionPublisher.PublishCount); // start + post-credit session
        Assert.Equal(1, _creditPublisher.PublishCount);

        var creditMsg = _creditPublisher.LastMessage!;
        Assert.Equal(1, creditMsg.SeqNo);
        Assert.Equal(1800, creditMsg.SecondsToAdd);
        Assert.Equal("coin", creditMsg.Source);
        Assert.Equal(20, creditMsg.Denomination);
    }

    [Fact]
    public async Task Tab_station_start_and_credit_flow_carries_deviceType_through_publishes()
    {
        // Regression guard added with the 2026-05-23 S25 Android bench:
        // the Kotlin client uses deviceType="tab" and the server's defaults
        // already support it, but no integration test previously exercised
        // the non-pc path end to end. Documents that pc/phn/tab share the
        // same code path and that the session publish carries the
        // station's deviceType verbatim.
        const string TabStationId = "tab-01";
        const string TabDeviceType = "tab";

        await _stationRepository.UpsertOnlineAsync(
            TabStationId,
            TabDeviceType,
            appVersion: "0.1.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));

        await _service.StartSessionAsync(TabStationId, TabDeviceType);
        var after = await _service.ApplyCreditAsync(
            TabStationId,
            secondsToAdd: 600,
            source: "coin",
            denomination: 10);

        Assert.False(after.IsLocked);
        Assert.Equal(600, after.RemainingSecondsAtAnchor);
        Assert.Equal(TabStationId, _sessionPublisher.LastState!.StationId);
        Assert.Equal(TabDeviceType, _sessionPublisher.LastState!.DeviceType);

        var creditMsg = _creditPublisher.LastMessage!;
        Assert.Equal("coin", creditMsg.Source);
        Assert.Equal(10, creditMsg.Denomination);
        Assert.Equal(600, creditMsg.SecondsToAdd);
    }

    [Fact]
    public async Task Consecutive_credits_mint_monotonic_seqnos()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        var first = await _service.ApplyCreditAsync(StationId, 30, source: "coin", denomination: 5);
        var second = await _service.ApplyCreditAsync(StationId, 30, source: "coin", denomination: 5);
        var third = await _service.ApplyCreditAsync(StationId, 30, source: "admin", denomination: null);

        Assert.Equal(1, first.LastCreditSeqNo);
        Assert.Equal(2, second.LastCreditSeqNo);
        Assert.Equal(3, third.LastCreditSeqNo);
        Assert.Equal(3, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task Admin_credit_carries_null_denomination()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, 600, source: "admin", denomination: null);

        var msg = _creditPublisher.LastMessage!;
        Assert.Equal("admin", msg.Source);
        Assert.Null(msg.Denomination);
    }

    [Fact]
    public async Task Live_remaining_decreases_as_monotonic_clock_advances()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 60, source: "coin", denomination: 1);
        Assert.Equal(60, _service.GetLiveRemainingSeconds(StationId));

        _clock.AdvanceSeconds(30);
        Assert.Equal(30, _service.GetLiveRemainingSeconds(StationId));

        _clock.AdvanceSeconds(30);
        Assert.Equal(0, _service.GetLiveRemainingSeconds(StationId));
    }

    [Fact]
    public async Task Tick_locks_session_when_budget_hits_zero()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 60, source: "coin", denomination: 1);
        var publishesBeforeFirstTick = _sessionPublisher.PublishCount;

        _clock.AdvanceSeconds(59);
        await _service.TickAsync();
        Assert.False(_sessionPublisher.LastState!.IsLocked);
        Assert.Equal(publishesBeforeFirstTick, _sessionPublisher.PublishCount);

        _clock.AdvanceSeconds(2);
        await _service.TickAsync();
        Assert.True(_sessionPublisher.LastState!.IsLocked);
        Assert.Equal(0, _sessionPublisher.LastState.RemainingSecondsAtAnchor);
        Assert.Equal(publishesBeforeFirstTick + 1, _sessionPublisher.PublishCount);
    }

    [Fact]
    public async Task Tick_after_lock_does_not_republish()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 5, source: "coin", denomination: 1);

        _clock.AdvanceSeconds(10);
        await _service.TickAsync();
        var publishesAfterLock = _sessionPublisher.PublishCount;

        _clock.AdvanceSeconds(60);
        await _service.TickAsync();
        await _service.TickAsync();

        Assert.Equal(publishesAfterLock, _sessionPublisher.PublishCount);
    }

    [Fact]
    public async Task Persistence_writes_only_on_transitions_not_on_idle_ticks()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        var afterStart = (await _sessionRepository.LoadActiveAsync()).Single();
        Assert.Equal(0, afterStart.RemainingSeconds);
        Assert.True(afterStart.IsLocked);

        await _service.ApplyCreditAsync(StationId, secondsToAdd: 30, source: "coin", denomination: 1);
        var afterCredit = (await _sessionRepository.LoadActiveAsync()).Single();
        Assert.Equal(30, afterCredit.RemainingSeconds);
        Assert.False(afterCredit.IsLocked);

        _clock.AdvanceSeconds(10);
        await _service.TickAsync();
        var afterIdleTick = (await _sessionRepository.LoadActiveAsync()).Single();
        Assert.Equal(afterCredit.UpdatedAt, afterIdleTick.UpdatedAt);
        Assert.Equal(30, afterIdleTick.RemainingSeconds);
        Assert.False(afterIdleTick.IsLocked);

        _clock.AdvanceSeconds(30);
        await _service.TickAsync();
        var afterLock = (await _sessionRepository.LoadActiveAsync()).Single();
        Assert.True(afterLock.IsLocked);
        Assert.Equal(0, afterLock.RemainingSeconds);
        Assert.NotEqual(afterCredit.UpdatedAt, afterLock.UpdatedAt);
    }

    [Fact]
    public async Task ApplyCredit_without_start_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ApplyCreditAsync(StationId, secondsToAdd: 30, source: "coin", denomination: 1));
    }

    [Fact]
    public async Task Live_remaining_unaffected_by_wall_clock_jump()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 600, source: "coin", denomination: 10);
        var beforeJump = _service.GetLiveRemainingSeconds(StationId);

        Thread.Sleep(10);
        var afterJump = _service.GetLiveRemainingSeconds(StationId);

        Assert.Equal(beforeJump, afterJump);

        _clock.AdvanceSeconds(120);
        Assert.Equal(480, _service.GetLiveRemainingSeconds(StationId));
    }

    [Fact]
    public async Task StartSession_adopts_existing_active_row_instead_of_inserting_duplicate()
    {
        // Simulate a prior process leaving an active row behind: insert directly via
        // the repository (bypassing SessionService so the cache stays empty), then
        // call StartSessionAsync. The new SessionService instance must adopt the row
        // — if it tried to INSERT a fresh sessionId, the partial unique index on
        // (station_id WHERE ended_at IS NULL) would throw and the pulse pipeline
        // would fail downstream as SessionStartFailed.
        var preExisting = new SessionRecord(
            SessionId: "s-survivor",
            StationId: StationId,
            StartedAt: FormatUtc(DateTimeOffset.UtcNow.AddMinutes(-30)),
            EndedAt: null,
            RemainingSeconds: 250,
            IsLocked: false,
            LastCreditSeqNo: 7,
            UpdatedAt: FormatUtc(DateTimeOffset.UtcNow.AddMinutes(-1)));
        await _sessionRepository.UpsertAsync(preExisting);

        // Fresh SessionService — cache starts empty, no MQTT publishes yet.
        var freshSessionPublisher = new FakeSessionPublisher();
        var freshCreditPublisher = new FakeCreditPublisher();
        var freshService = new SessionService(
            _sessionRepository,
            freshSessionPublisher,
            freshCreditPublisher,
            _clock);
        await freshService.InitializeAsync();

        var adopted = await freshService.StartSessionAsync(StationId, DeviceType);

        Assert.Equal("s-survivor", adopted.SessionId);
        Assert.Equal(7, adopted.LastCreditSeqNo);
        Assert.Equal(250, adopted.RemainingSecondsAtAnchor);
        Assert.False(adopted.IsLocked);

        // Adoption is a cache-only operation — no MQTT publish, no extra DB write.
        Assert.Equal(0, freshSessionPublisher.PublishCount);
        Assert.Equal(0, freshCreditPublisher.PublishCount);

        // A subsequent credit should now succeed using the adopted seqno baseline.
        var afterCredit = await freshService.ApplyCreditAsync(StationId, 30, source: "coin", denomination: 1);
        Assert.Equal(8, afterCredit.LastCreditSeqNo);
        Assert.Equal(280, afterCredit.RemainingSecondsAtAnchor);
    }

    [Fact]
    public async Task PreloadActiveSessions_locks_persists_and_publishes_expired_unlocked_row()
    {
        var staleUpdatedAt = FormatUtc(DateTimeOffset.UtcNow.AddMinutes(-10));
        await _sessionRepository.UpsertAsync(new SessionRecord(
            SessionId: "s-expired",
            StationId: StationId,
            StartedAt: FormatUtc(DateTimeOffset.UtcNow.AddMinutes(-20)),
            EndedAt: null,
            RemainingSeconds: 60,
            IsLocked: false,
            LastCreditSeqNo: 7,
            UpdatedAt: staleUpdatedAt));

        var freshSessionPublisher = new FakeSessionPublisher();
        var freshCreditPublisher = new FakeCreditPublisher();
        var freshService = new SessionService(
            _sessionRepository,
            freshSessionPublisher,
            freshCreditPublisher,
            _clock);
        await freshService.InitializeAsync();

        await freshService.PreloadActiveSessionsAsync(
            new Dictionary<string, string> { [StationId] = DeviceType });

        Assert.True(freshService.TryGetState(StationId, out var state));
        Assert.True(state.IsLocked);
        Assert.Equal(0, state.RemainingSecondsAtAnchor);

        var persisted = await _sessionRepository.GetActiveForStationAsync(StationId);
        Assert.NotNull(persisted);
        Assert.True(persisted!.IsLocked);
        Assert.Equal(0, persisted.RemainingSeconds);
        Assert.NotEqual(staleUpdatedAt, persisted.UpdatedAt);

        Assert.Equal(1, freshSessionPublisher.PublishCount);
        Assert.True(freshSessionPublisher.LastState!.IsLocked);
        Assert.Equal(0, freshSessionPublisher.LastState.RemainingSecondsAtAnchor);
        Assert.Equal(0, freshCreditPublisher.PublishCount);
    }

    [Fact]
    public async Task Credit_after_partial_decay_extends_correctly()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 60, source: "coin", denomination: 1);

        _clock.AdvanceSeconds(20);
        var afterSecond = await _service.ApplyCreditAsync(StationId, secondsToAdd: 100, source: "coin", denomination: 5);

        Assert.Equal(140, afterSecond.RemainingSecondsAtAnchor);
        Assert.Equal(140, _service.GetLiveRemainingSeconds(StationId));
    }

    [Fact]
    public async Task ClearSession_locks_and_publishes_without_minting_credit()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 600, source: "coin", denomination: 10);

        var cleared = await _service.ClearSessionAsync(StationId, DeviceType);

        Assert.NotNull(cleared);
        Assert.True(cleared.IsLocked);
        Assert.Equal(0, cleared.RemainingSecondsAtAnchor);
        Assert.Equal(1, cleared.LastCreditSeqNo);
        Assert.Equal(3, _sessionPublisher.PublishCount); // start + credit + clear
        Assert.Equal(1, _creditPublisher.PublishCount);

        var persisted = await _sessionRepository.GetActiveForStationAsync(StationId);
        Assert.NotNull(persisted);
        Assert.True(persisted.IsLocked);
        Assert.Equal(0, persisted.RemainingSeconds);
    }

    [Fact]
    public async Task RemoveTime_subtracts_without_bumping_credit_seqno()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 1800, source: "coin", denomination: 5);
        // seq=1 after the credit. Remove should NOT bump it.

        var result = await _service.RemoveTimeAsync(StationId, DeviceType, secondsToRemove: 600);

        Assert.NotNull(result);
        Assert.Equal(600, result!.ActualRemovedSec);
        Assert.False(result.NowLocked);
        Assert.Equal(1200, result.Session.RemainingSecondsAtAnchor);
        Assert.Equal(1, result.Session.LastCreditSeqNo); // not bumped
        // start + credit + remove = 3 retained session publishes.
        Assert.Equal(3, _sessionPublisher.PublishCount);
        Assert.Equal(1, _creditPublisher.PublishCount); // no extra credit minted
        Assert.False(result.Session.IsLocked);
    }

    [Fact]
    public async Task RemoveTime_clamps_at_zero_and_relocks()
    {
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 300, source: "coin", denomination: 1);

        var result = await _service.RemoveTimeAsync(StationId, DeviceType, secondsToRemove: 9999);

        Assert.NotNull(result);
        Assert.Equal(300, result!.ActualRemovedSec); // clamped to what was actually available
        Assert.True(result.NowLocked);
        Assert.Equal(0, result.Session.RemainingSecondsAtAnchor);
        Assert.True(result.Session.IsLocked);
    }

    [Fact]
    public async Task RemoveTime_on_session_already_at_zero_is_noop()
    {
        // Start a session but never credit it — it's locked at zero.
        await _service.StartSessionAsync(StationId, DeviceType);

        var result = await _service.RemoveTimeAsync(StationId, DeviceType, secondsToRemove: 600);

        Assert.NotNull(result);
        Assert.Equal(0, result!.ActualRemovedSec);
        Assert.True(result.NowLocked); // was locked, still locked
        Assert.Equal(1, _sessionPublisher.PublishCount); // only the original start
    }

    [Fact]
    public async Task RemoveTime_returns_null_when_no_session_exists()
    {
        var result = await _service.RemoveTimeAsync(StationId, DeviceType, secondsToRemove: 60);

        Assert.Null(result);
        Assert.Equal(0, _sessionPublisher.PublishCount);
    }

    [Fact]
    public async Task RemoveTime_after_partial_decay_uses_live_remaining()
    {
        // Credit 600 s, let 200 s tick by, then remove 100 s. Expect new live
        // remaining = (600 - 200) - 100 = 300, and the new retained value
        // anchors at the post-decay number, not the original 600.
        await _service.StartSessionAsync(StationId, DeviceType);
        await _service.ApplyCreditAsync(StationId, secondsToAdd: 600, source: "coin", denomination: 5);

        _clock.AdvanceSeconds(200);

        var result = await _service.RemoveTimeAsync(StationId, DeviceType, secondsToRemove: 100);

        Assert.NotNull(result);
        Assert.Equal(100, result!.ActualRemovedSec);
        Assert.Equal(300, result.Session.RemainingSecondsAtAnchor);
        Assert.Equal(300, _service.GetLiveRemainingSeconds(StationId));
        Assert.False(result.NowLocked);
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
        catch
        {
        }
    }
}
