using System.Globalization;
using RentalServer.Core.Admin;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;
using RentalServer.Core.Tests.Sessions;

namespace RentalServer.Core.Tests.Admin;

public sealed class AdminTimeServiceTests : IAsyncLifetime
{
    private const string StationId = "pc-01";
    private const string DeviceType = "pc";

    private string _databasePath = string.Empty;
    private StationRepository _stationRepository = null!;
    private SessionRepository _sessionRepository = null!;
    private FakeMonotonicClock _clock = null!;
    private FakeSessionPublisher _sessionPublisher = null!;
    private FakeCreditPublisher _creditPublisher = null!;
    private SessionService _sessionService = null!;
    private AdminTimeService _adminTimeService = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");
        _stationRepository = new StationRepository(_databasePath);
        _sessionRepository = new SessionRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _sessionRepository.InitializeAsync();

        _clock = new FakeMonotonicClock();
        _sessionPublisher = new FakeSessionPublisher();
        _creditPublisher = new FakeCreditPublisher();
        _sessionService = new SessionService(
            _sessionRepository,
            _sessionPublisher,
            _creditPublisher,
            _clock);
        await _sessionService.InitializeAsync();

        _adminTimeService = new AdminTimeService(_stationRepository, _sessionService);
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddTime_online_locked_station_unlocks_with_admin_credit()
    {
        await SeedOnlineStationAsync();

        var result = await _adminTimeService.AddTimeAsync(StationId, secondsToAdd: 600);

        Assert.True(result.Applied);
        Assert.Equal(AdminAddTimeStatus.Applied, result.Status);
        Assert.NotNull(result.Session);
        Assert.False(result.Session.IsLocked);
        Assert.Equal(600, result.Session.RemainingSecondsAtAnchor);
        Assert.Equal(1, result.Session.LastCreditSeqNo);
        Assert.Equal("admin", _creditPublisher.LastMessage!.Source);
        Assert.Null(_creditPublisher.LastMessage.Denomination);
        Assert.Equal(2, _sessionPublisher.PublishCount); // start locked + admin unlock
        Assert.Equal(1, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task AddTime_online_active_station_extends_live_remaining()
    {
        await SeedOnlineStationAsync();
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        await _sessionService.ApplyCreditAsync(StationId, 600, source: "coin", denomination: 10);
        _clock.AdvanceSeconds(120);

        var result = await _adminTimeService.AddTimeAsync(StationId, secondsToAdd: 300);

        Assert.True(result.Applied);
        Assert.False(result.Session!.IsLocked);
        Assert.Equal(780, result.Session.RemainingSecondsAtAnchor);
        Assert.Equal(2, result.Session.LastCreditSeqNo);
        Assert.Equal(780, _sessionService.GetLiveRemainingSeconds(StationId));
        Assert.Equal("admin", _creditPublisher.LastMessage!.Source);
    }

    [Fact]
    public async Task AddTime_offline_station_is_applied()
    {
        await SeedOnlineStationAsync();
        await _stationRepository.MarkOfflineAsync(StationId, FormatUtc(DateTimeOffset.UtcNow));

        var result = await _adminTimeService.AddTimeAsync(StationId, secondsToAdd: 600);

        Assert.True(result.Applied);
        Assert.Equal(AdminAddTimeStatus.Applied, result.Status);
        Assert.NotNull(result.Session);
        Assert.False(result.Session.IsLocked);
        Assert.Equal(600, result.Session.RemainingSecondsAtAnchor);
        Assert.Equal(2, _sessionPublisher.PublishCount);
        Assert.Equal(1, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task AddTime_unknown_station_is_blocked()
    {
        var result = await _adminTimeService.AddTimeAsync("pc-missing", secondsToAdd: 600);

        Assert.False(result.Applied);
        Assert.Equal(AdminAddTimeStatus.StationUnknown, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(0, _sessionPublisher.PublishCount);
        Assert.Equal(0, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task RemoveTime_online_active_session_subtracts_and_returns_applied()
    {
        await SeedOnlineStationAsync();
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        await _sessionService.ApplyCreditAsync(StationId, 1800, source: "coin", denomination: 5);

        var result = await _adminTimeService.RemoveTimeAsync(StationId, secondsToRemove: 600);

        Assert.True(result.Applied);
        Assert.Equal(AdminRemoveTimeStatus.Applied, result.Status);
        Assert.Equal(600, result.ActualRemovedSec);
        Assert.False(result.NowLocked);
        Assert.NotNull(result.Session);
        Assert.Equal(1200, result.Session!.RemainingSecondsAtAnchor);
        Assert.Equal(1, result.Session.LastCreditSeqNo); // unchanged — Remove doesn't bump credit seq
    }

    [Fact]
    public async Task RemoveTime_clamps_at_zero_and_relocks()
    {
        await SeedOnlineStationAsync();
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        await _sessionService.ApplyCreditAsync(StationId, 300, source: "coin", denomination: 1);

        var result = await _adminTimeService.RemoveTimeAsync(StationId, secondsToRemove: 999_999);

        Assert.True(result.Applied);
        Assert.Equal(300, result.ActualRemovedSec); // only what was there
        Assert.True(result.NowLocked);
        Assert.Equal(0, result.Session!.RemainingSecondsAtAnchor);
    }

    [Fact]
    public async Task RemoveTime_with_no_active_session_returns_NoActiveSession()
    {
        await SeedOnlineStationAsync();
        // Station registered + online, but no session ever started.

        var result = await _adminTimeService.RemoveTimeAsync(StationId, secondsToRemove: 60);

        Assert.False(result.Applied);
        Assert.Equal(AdminRemoveTimeStatus.NoActiveSession, result.Status);
        Assert.Equal(0, result.ActualRemovedSec);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task RemoveTime_against_zero_session_returns_NothingToRemove()
    {
        await SeedOnlineStationAsync();
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        // Session started + locked at zero, never credited.

        var result = await _adminTimeService.RemoveTimeAsync(StationId, secondsToRemove: 60);

        Assert.False(result.Applied);
        Assert.Equal(AdminRemoveTimeStatus.NothingToRemove, result.Status);
        Assert.Equal(0, result.ActualRemovedSec);
        Assert.True(result.NowLocked);
    }

    [Fact]
    public async Task RemoveTime_offline_station_is_applied()
    {
        await SeedOnlineStationAsync();
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        await _sessionService.ApplyCreditAsync(StationId, secondsToAdd: 900, source: "coin", denomination: 10);
        await _stationRepository.MarkOfflineAsync(StationId, FormatUtc(DateTimeOffset.UtcNow));

        var result = await _adminTimeService.RemoveTimeAsync(StationId, secondsToRemove: 600);

        Assert.True(result.Applied);
        Assert.Equal(AdminRemoveTimeStatus.Applied, result.Status);
        Assert.Equal(600, result.ActualRemovedSec);
        Assert.False(result.NowLocked);
        Assert.Equal(300, result.Session!.RemainingSecondsAtAnchor);
    }

    [Fact]
    public async Task RemoveTime_unknown_station_is_blocked()
    {
        var result = await _adminTimeService.RemoveTimeAsync("pc-missing", secondsToRemove: 60);

        Assert.False(result.Applied);
        Assert.Equal(AdminRemoveTimeStatus.StationUnknown, result.Status);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task TransferTime_moves_live_remaining_to_locked_target_without_minting_credit()
    {
        await SeedOnlineStationAsync("pc-01");
        await SeedOnlineStationAsync("pc-02");
        await _sessionService.StartSessionAsync("pc-01", DeviceType);
        await _sessionService.ApplyCreditAsync("pc-01", 1800, source: "coin", denomination: 10);
        await _sessionService.StartSessionAsync("pc-02", DeviceType);
        _clock.AdvanceSeconds(300);

        var result = await _adminTimeService.TransferTimeAsync("pc-01", "pc-02");

        Assert.True(result.Applied);
        Assert.Equal(AdminTransferTimeStatus.Applied, result.Status);
        Assert.Equal(1500, result.SecondsTransferred);
        Assert.True(result.SourceSession!.IsLocked);
        Assert.Equal(0, result.SourceSession.RemainingSecondsAtAnchor);
        Assert.False(result.TargetSession!.IsLocked);
        Assert.Equal(1500, result.TargetSession.RemainingSecondsAtAnchor);
        Assert.Equal(0, _sessionService.GetLiveRemainingSeconds("pc-01"));
        Assert.Equal(1500, _sessionService.GetLiveRemainingSeconds("pc-02"));
        Assert.Equal(1, _creditPublisher.PublishCount); // transfer republishes sessions only
    }

    [Fact]
    public async Task TransferTime_blocks_when_target_already_has_time()
    {
        await SeedOnlineStationAsync("pc-01");
        await SeedOnlineStationAsync("pc-02");
        await _sessionService.StartSessionAsync("pc-01", DeviceType);
        await _sessionService.ApplyCreditAsync("pc-01", 1800, source: "coin", denomination: 10);
        await _sessionService.StartSessionAsync("pc-02", DeviceType);
        await _sessionService.ApplyCreditAsync("pc-02", 600, source: "coin", denomination: 5);

        var result = await _adminTimeService.TransferTimeAsync("pc-01", "pc-02");

        Assert.False(result.Applied);
        Assert.Equal(AdminTransferTimeStatus.TargetHasTime, result.Status);
        Assert.Equal(1800, _sessionService.GetLiveRemainingSeconds("pc-01"));
        Assert.Equal(600, _sessionService.GetLiveRemainingSeconds("pc-02"));
    }

    private Task SeedOnlineStationAsync(string stationId = StationId, string deviceType = DeviceType)
    {
        return _stationRepository.UpsertOnlineAsync(
            stationId,
            deviceType,
            appVersion: "1.0.0",
            lastSeenAt: FormatUtc(DateTimeOffset.UtcNow));
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
