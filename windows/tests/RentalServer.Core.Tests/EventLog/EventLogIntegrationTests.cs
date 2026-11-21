using System.Globalization;
using RentalServer.Core.Acceptors;
using RentalServer.Core.Admin;
using RentalServer.Core.Claims;
using RentalServer.Core.EventLog;
using RentalServer.Core.Rates;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;
using RentalServer.Core.Tests.Acceptors;
using RentalServer.Core.Tests.Claims;
using RentalServer.Core.Tests.Rates;
using RentalServer.Core.Tests.Sessions;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.EventLog;

/// <summary>
/// End-to-end-on-fakes integration: every service plugged into a single
/// RecordingEventLogger so we can prove the wiring emits exactly the
/// right row shape for each event type. The hash chain mechanics live
/// in EventLogRepositoryTests; this suite only cares that each service
/// hands off the right (event_type, station_id, acceptor_id, amount_pesos,
/// seconds_credited) tuple.
/// </summary>
public sealed class EventLogIntegrationTests : IAsyncLifetime
{
    private const string StationId = "pc-01";
    private const string DeviceType = "pc";
    private const string AcceptorId = "coin-01";

    private string _databasePath = string.Empty;
    private StationRepository _stationRepository = null!;
    private SessionRepository _sessionRepository = null!;
    private RateRepository _rateRepository = null!;
    private AcceptorRepository _acceptorRepository = null!;
    private ClaimRepository _claimRepository = null!;
    private FakeMonotonicClock _clock = null!;
    private FakeSessionPublisher _sessionPublisher = null!;
    private FakeCreditPublisher _creditPublisher = null!;
    private FakeClaimResultPublisher _claimResultPublisher = null!;
    private FakeLedPublisher _ledPublisher = null!;
    private FakePulseAckPublisher _pulseAckPublisher = null!;
    private FakeRatesConfigPublisher _ratesPublisher = null!;
    private RecordingEventLogger _logger = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-eventlog-int-{Guid.NewGuid():N}.db");
        _stationRepository = new StationRepository(_databasePath);
        _sessionRepository = new SessionRepository(_databasePath);
        _rateRepository = new RateRepository(_databasePath);
        _acceptorRepository = new AcceptorRepository(_databasePath);
        _claimRepository = new ClaimRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _sessionRepository.InitializeAsync();
        await _rateRepository.InitializeAsync();
        await _rateRepository.EnsureSeededAsync(FormatUtcNow());
        await _acceptorRepository.InitializeAsync();
        await _claimRepository.InitializeAsync();

        _clock = new FakeMonotonicClock();
        _sessionPublisher = new FakeSessionPublisher();
        _creditPublisher = new FakeCreditPublisher();
        _claimResultPublisher = new FakeClaimResultPublisher();
        _ledPublisher = new FakeLedPublisher();
        _pulseAckPublisher = new FakePulseAckPublisher();
        _ratesPublisher = new FakeRatesConfigPublisher();
        _logger = new RecordingEventLogger();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Admin_credit_emits_admin_credit_row()
    {
        await SeedOnlineStationAsync();
        var sessionService = new SessionService(
            _sessionRepository,
            _sessionPublisher,
            _creditPublisher,
            _clock,
            _logger);
        var adminService = new AdminTimeService(_stationRepository, sessionService, _logger);

        await adminService.AddTimeAsync(StationId, secondsToAdd: 600);

        Assert.Equal(1, _logger.Count);
        var row = _logger.Last!;
        Assert.Equal(EventTypes.AdminCredit, row.EventType);
        Assert.Equal(StationId, row.StationId);
        Assert.Null(row.AcceptorId);
        Assert.Null(row.AmountPesos);
        Assert.Equal(600, row.SecondsCredited);
    }

    [Fact]
    public async Task ClearSession_emits_session_cleared_row_with_previous_remaining()
    {
        // S22 follow-up to S20.9: Codex's ClearSessionAsync originally skipped the
        // event_log; the bench discovered the audit hole when a force-wipe left no
        // trace. This test asserts the new session_cleared row.
        await SeedOnlineStationAsync();
        var sessionService = new SessionService(
            _sessionRepository,
            _sessionPublisher,
            _creditPublisher,
            _clock,
            _logger);

        await sessionService.StartSessionAsync(StationId, DeviceType);
        await sessionService.ApplyCreditAsync(StationId, secondsToAdd: 600, source: "admin", denomination: null);

        var sessionsBefore = _logger.Count;
        var cleared = await sessionService.ClearSessionAsync(StationId, DeviceType);

        Assert.NotNull(cleared);
        Assert.True(cleared!.IsLocked);
        Assert.Equal(0, cleared.RemainingSecondsAtAnchor);

        Assert.Equal(sessionsBefore + 1, _logger.Count);
        var row = _logger.Last!;
        Assert.Equal(EventTypes.SessionCleared, row.EventType);
        Assert.Equal(StationId, row.StationId);
        Assert.Null(row.AcceptorId);
        Assert.Null(row.AmountPesos);
        Assert.Null(row.SecondsCredited);
        // The payload should preserve the previousRemainingSec so an audit reader
        // knows how much time was wiped.
        Assert.Contains("previousRemainingSec", row.Payload);
    }

    [Fact]
    public async Task Rate_publish_emits_rate_published_row()
    {
        var rateService = new RateService(_rateRepository, _ratesPublisher, _logger);

        var newRates = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["pc"]  = new Dictionary<int, int> { [1] = 60,  [5] = 360,  [10] = 750,  [20] = 1800 },
            ["phn"] = new Dictionary<int, int> { [1] = 120, [5] = 720,  [10] = 1500, [20] = 3600 },
            ["tab"] = new Dictionary<int, int> { [1] = 90,  [5] = 540,  [10] = 1125, [20] = 2700 }
        };

        await rateService.UpdateRatesAsync(newRates);

        Assert.Equal(1, _logger.Count);
        var row = _logger.Last!;
        Assert.Equal(EventTypes.RatePublished, row.EventType);
        Assert.Null(row.StationId);
        Assert.Null(row.AcceptorId);
        Assert.Null(row.AmountPesos);
        Assert.Null(row.SecondsCredited);
    }

    [Fact]
    public async Task Claim_grant_then_cancel_emits_granted_and_cancelled_rows()
    {
        // Station row needed so claim's FK to stations is satisfied — otherwise
        // ClaimService falls into the station_unknown deny path silently.
        await SeedOnlineStationAsync();
        await SeedOnlineAcceptorAsync();

        var claimService = new ClaimService(
            _claimRepository,
            _acceptorRepository,
            _claimResultPublisher,
            _ledPublisher,
            claimWindowSeconds: 60,
            ledAutoOffSeconds: 65,
            eventLogger: _logger);
        await claimService.InitializeAsync();

        // Grant a claim.
        await claimService.ProcessClaimRequestAsync(
            StationId,
            new ClaimRequestMessage
            {
                RequestId = "r-1",
                StationId = StationId,
                RequestedAt = FormatUtcNow()
            });

        Assert.Equal(1, _logger.Count);
        Assert.Equal(EventTypes.ClaimGranted, _logger.Last!.EventType);
        Assert.Equal(StationId, _logger.Last.StationId);
        Assert.Equal(AcceptorId, _logger.Last.AcceptorId);

        // Resolve with no credits → cancelled.
        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        await claimService.ProcessClaimEndAsync(
            StationId,
            new ClaimEndMessage
            {
                RequestId = "r-1",
                ClaimId = pending!.ClaimId,
                StationId = StationId,
                EndedAt = FormatUtcNow()
            });

        Assert.Equal(2, _logger.Count);
        Assert.Equal(EventTypes.ClaimCancelled, _logger.Last!.EventType);
        // No credits accumulated → seconds_credited stays null on cancel.
        Assert.Null(_logger.Last.SecondsCredited);
    }

    private Task SeedOnlineStationAsync()
    {
        return _stationRepository.UpsertOnlineAsync(
            StationId,
            DeviceType,
            appVersion: "1.0.0",
            lastSeenAt: FormatUtcNow());
    }

    private Task SeedOnlineAcceptorAsync()
    {
        return _acceptorRepository.UpsertOnlineAsync(
            AcceptorId,
            firmwareVersion: "1.0.0",
            lastRssi: -50,
            lastSeenAt: FormatUtcNow());
    }

    private static string FormatUtcNow()
    {
        return DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
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
