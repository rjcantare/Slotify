using System.Globalization;
using System.Text.Json;
using RentalServer.Core.Acceptors;
using RentalServer.Core.Claims;
using RentalServer.Core.EventLog;
using RentalServer.Core.Rates;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;
using RentalServer.Core.Tests.Claims;
using RentalServer.Core.Tests.EventLog;
using RentalServer.Core.Tests.Sessions;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Acceptors;

public sealed class PulseServiceTests : IAsyncLifetime
{
    private const string StationId = "pc-01";
    private const string AcceptorId = "coin-01";
    private const string DeviceType = "pc";

    private string _databasePath = string.Empty;
    private StationRepository _stationRepository = null!;
    private AcceptorRepository _acceptorRepository = null!;
    private ClaimRepository _claimRepository = null!;
    private RateRepository _rateRepository = null!;
    private SessionRepository _sessionRepository = null!;

    private FakeSessionPublisher _sessionPublisher = null!;
    private FakeCreditPublisher _creditPublisher = null!;
    private FakeClaimResultPublisher _claimResultPublisher = null!;
    private FakeLedPublisher _ledPublisher = null!;
    private FakePulseAckPublisher _pulseAckPublisher = null!;
    private FakeMonotonicClock _clock = null!;
    private RecordingEventLogger _eventLogger = null!;

    private SessionService _sessionService = null!;
    private ClaimService _claimService = null!;
    private PulseService _pulseService = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");

        _stationRepository = new StationRepository(_databasePath);
        _acceptorRepository = new AcceptorRepository(_databasePath);
        _claimRepository = new ClaimRepository(_databasePath);
        _rateRepository = new RateRepository(_databasePath);
        _sessionRepository = new SessionRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _acceptorRepository.InitializeAsync();
        await _claimRepository.InitializeAsync();
        await _rateRepository.InitializeAsync();
        await _rateRepository.EnsureSeededAsync(FormatUtcNow());
        await _sessionRepository.InitializeAsync();

        await _stationRepository.UpsertOnlineAsync(StationId, DeviceType, "dev", FormatUtcNow());
        await _acceptorRepository.UpsertOnlineAsync(AcceptorId, "fw-1", -50, FormatUtcNow());

        _sessionPublisher = new FakeSessionPublisher();
        _creditPublisher = new FakeCreditPublisher();
        _claimResultPublisher = new FakeClaimResultPublisher();
        _ledPublisher = new FakeLedPublisher();
        _pulseAckPublisher = new FakePulseAckPublisher();
        _clock = new FakeMonotonicClock();
        _eventLogger = new RecordingEventLogger();

        _sessionService = new SessionService(_sessionRepository, _sessionPublisher, _creditPublisher, _clock);
        _claimService = new ClaimService(
            _claimRepository,
            _acceptorRepository,
            _claimResultPublisher,
            _ledPublisher,
            claimWindowSeconds: 30,
            ledAutoOffSeconds: 35,
            resolveRateModeAsync: (stationId, _) =>
                Task.FromResult(_sessionService.GetLiveRemainingSeconds(stationId) is > 0
                    ? ClaimRateMode.AddTime
                    : ClaimRateMode.InsertCoin),
            applyResolvedCreditAsync: async (claim, seconds, ct) =>
            {
                var station = await _stationRepository.GetAsync(claim.StationId, ct);
                Assert.NotNull(station);
                await _sessionService.StartSessionAsync(claim.StationId, station!.DeviceType, ct);
                await _sessionService.ApplyDeferredCreditAsync(
                    claim.StationId,
                    seconds,
                    cancellationToken: ct);
            });
        _pulseService = new PulseService(
            _acceptorRepository,
            _claimRepository,
            _stationRepository,
            _rateRepository,
            _sessionService,
            _claimService,
            _pulseAckPublisher,
            _eventLogger);

        await _sessionService.InitializeAsync();
        await _claimService.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    private async Task<ClaimRecord> GrantClaimAsync(string requestId = "r-1")
    {
        await _claimService.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = requestId,
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(pending);
        return pending!;
    }

    [Fact]
    public async Task Locked_insert_coin_pulse_accumulates_without_updating_session_until_done()
    {
        // Locked Insert Coin records the total during the window, but does not add
        // session time until DONE / TTL closes the claim.
        await GrantClaimAsync();

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 20,
            PulseCount = 4,
            MillisSinceBoot = 1234
        });

        Assert.True(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.None, outcome.Reason);
        Assert.Equal(7200, outcome.SecondsCredited);

        Assert.Equal(1, _pulseAckPublisher.PublishCount);
        Assert.Equal(1, _creditPublisher.PublishCount);
        Assert.Equal(7200, _creditPublisher.LastMessage!.SecondsToAdd);

        Assert.Equal(1, _sessionPublisher.PublishCount);
        Assert.True(_sessionPublisher.LastState!.IsLocked);
        Assert.Equal(0, _sessionPublisher.LastState.RemainingSecondsAtAnchor);

        // Claim still pending; accumulated_credits has the two-hour locked credit.
        var stillPending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(stillPending);
        Assert.Equal(7200, await _claimRepository.GetAccumulatedCreditsAsync(stillPending!.ClaimId));
    }

    [Fact]
    public async Task Duplicate_seqno_acks_but_does_not_credit()
    {
        await GrantClaimAsync();
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 5,
            Denomination = 20,
            PulseCount = 4
        });
        var creditsBefore = _creditPublisher.PublishCount;

        var dup = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 5,
            Denomination = 20,
            PulseCount = 4
        });

        Assert.False(dup.Credited);
        Assert.Equal(PulseService.RejectReason.DuplicateOrStaleSeqNo, dup.Reason);
        Assert.Equal(2, _pulseAckPublisher.PublishCount); // ACKed both times
        Assert.Equal(creditsBefore, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task Stale_seqno_acks_but_does_not_credit()
    {
        await GrantClaimAsync();
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 10,
            Denomination = 5,
            PulseCount = 2
        });
        var creditsBefore = _creditPublisher.PublishCount;

        var stale = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 3,
            Denomination = 20,
            PulseCount = 4
        });

        Assert.False(stale.Credited);
        Assert.Equal(PulseService.RejectReason.DuplicateOrStaleSeqNo, stale.Reason);
        Assert.Equal(creditsBefore, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task Orphan_pulse_without_active_claim_acks_no_credit()
    {
        // No claim granted before this pulse.
        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 5,
            PulseCount = 2
        });

        Assert.False(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.NoActiveClaim, outcome.Reason);
        Assert.Equal(1, _pulseAckPublisher.PublishCount);
        Assert.Equal(0, _creditPublisher.PublishCount);
        Assert.Equal(0, _sessionPublisher.PublishCount);
    }

    [Fact]
    public async Task Unknown_denomination_acks_no_credit()
    {
        await GrantClaimAsync();

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 99, // not in the seeded rate sheet
            PulseCount = 9
        });

        Assert.False(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.UnknownDenomination, outcome.Reason);
        Assert.Equal(1, _pulseAckPublisher.PublishCount);
        Assert.Equal(0, _creditPublisher.PublishCount);
    }

    [Fact]
    public async Task Successful_pulse_keeps_claim_pending_and_led_on()
    {
        // S18a: pulse no longer fulfills. LED stays on for the multi-coin window.
        await GrantClaimAsync();
        Assert.Equal(1, _ledPublisher.PublishCount);
        Assert.Equal("on", _ledPublisher.LastMessage!.State);

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 10,
            PulseCount = 3
        });

        // Still just the "on" LED publish; no "off" yet — that fires on claim/end / TTL.
        Assert.Equal(1, _ledPublisher.PublishCount);
        Assert.Equal("on", _ledPublisher.LastMessage!.State);

        var stillPending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(stillPending);
    }

    [Fact]
    public async Task Locked_multi_coin_window_applies_total_only_on_claim_end()
    {
        // Two coins land within one locked claim. The session stays locked until
        // DONE applies the accumulated total once.
        var claim = await GrantClaimAsync();

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1, Denomination = 20, PulseCount = 4
        });
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 2, Denomination = 5, PulseCount = 2
        });

        Assert.Equal(2, _creditPublisher.PublishCount);
        Assert.Equal(1, _sessionPublisher.PublishCount);
        Assert.True(_sessionPublisher.LastState!.IsLocked);
        Assert.Equal(9000, await _claimRepository.GetAccumulatedCreditsAsync(claim.ClaimId));

        await _claimService.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = claim.RequestId,
            ClaimId = claim.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        // DONE applies the accumulated total once and closes the coin window.
        Assert.Equal(2, _creditPublisher.PublishCount);
        Assert.Equal(2, _sessionPublisher.PublishCount);
        Assert.False(_sessionPublisher.LastState!.IsLocked);
        Assert.Equal(9000, _sessionPublisher.LastState.RemainingSecondsAtAnchor);

        // Claim is resolved + LED off.
        Assert.Null(await _claimRepository.GetPendingForAcceptorAsync(AcceptorId));
        Assert.Equal(2, _ledPublisher.PublishCount);
        Assert.Equal("off", _ledPublisher.LastMessage!.State);
    }

    [Fact]
    public async Task Locked_insert_coin_totals_follow_starter_then_per_peso_rule()
    {
        await GrantClaimAsync();

        var pulse = 1L;
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = pulse++, Denomination = 5, PulseCount = 2
        });
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = pulse++, Denomination = 1, PulseCount = 1
        });

        Assert.Equal(2, _creditPublisher.PublishCount);
        Assert.True(_sessionPublisher.LastState!.IsLocked);

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = pulse++, Denomination = 1, PulseCount = 1
        });

        Assert.Equal(1800, await _claimRepository.GetAccumulatedCreditsAsync(
            (await _claimRepository.GetPendingForAcceptorAsync(AcceptorId))!.ClaimId));
        Assert.Equal(3, _creditPublisher.PublishCount);
        Assert.True(_sessionPublisher.LastState!.IsLocked);

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = pulse++, Denomination = 1, PulseCount = 1
        });
        Assert.Equal(2160, await _claimRepository.GetAccumulatedCreditsAsync(
            (await _claimRepository.GetPendingForAcceptorAsync(AcceptorId))!.ClaimId));

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = pulse++, Denomination = 1, PulseCount = 1
        });
        Assert.Equal(2520, await _claimRepository.GetAccumulatedCreditsAsync(
            (await _claimRepository.GetPendingForAcceptorAsync(AcceptorId))!.ClaimId));

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = pulse, Denomination = 1, PulseCount = 1
        });
        var claim = (await _claimRepository.GetPendingForAcceptorAsync(AcceptorId))!;
        Assert.Equal(3600, await _claimRepository.GetAccumulatedCreditsAsync(claim.ClaimId));

        await _claimService.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = claim.RequestId,
            ClaimId = claim.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        Assert.Equal(6, _creditPublisher.PublishCount);
        Assert.False(_sessionPublisher.LastState!.IsLocked);
        Assert.Equal(3600, _sessionPublisher.LastState.RemainingSecondsAtAnchor);
    }

    [Fact]
    public async Task Locked_insert_coin_accepts_merged_peso_count_burst()
    {
        await GrantClaimAsync();

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 7,
            PulseCount = 7
        });

        Assert.True(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.None, outcome.Reason);
        Assert.Equal(1800, outcome.SecondsCredited);
        Assert.Equal(7, _creditPublisher.LastMessage!.Denomination);

        var claim = (await _claimRepository.GetPendingForAcceptorAsync(AcceptorId))!;
        Assert.Equal(7, await _claimRepository.GetAccumulatedPesosAsync(claim.ClaimId));
        Assert.Equal(1800, await _claimRepository.GetAccumulatedCreditsAsync(claim.ClaimId));
    }

    [Fact]
    public async Task Locked_insert_coin_logs_each_physical_coin_drop_for_history()
    {
        await GrantClaimAsync();

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 5,
            PulseCount = 5
        });
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 2,
            Denomination = 5,
            PulseCount = 5
        });

        var coinRows = _eventLogger.Appended
            .Where(e => e.EventType == EventTypes.CoinCredit)
            .ToList();

        Assert.Equal(2, coinRows.Count);
        Assert.Equal(5, coinRows[0].AmountPesos);
        Assert.Null(coinRows[0].SecondsCredited);
        Assert.Equal(5, coinRows[1].AmountPesos);
        Assert.Equal(3600, coinRows[1].SecondsCredited);
        Assert.All(coinRows, row => Assert.Contains("\"rateMode\":\"insert_coin\"", row.Payload));
    }

    [Fact]
    public async Task Add_time_accepts_merged_peso_count_burst()
    {
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        await _sessionService.ApplyCreditAsync(StationId, 600, source: "admin", denomination: null);
        await GrantClaimAsync();

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 7,
            PulseCount = 7
        });

        Assert.True(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.None, outcome.Reason);
        Assert.Equal(2520, outcome.SecondsCredited);
        Assert.Equal(7, _creditPublisher.LastMessage!.Denomination);
        Assert.Equal(3120, _sessionPublisher.LastState!.RemainingSecondsAtAnchor);
    }

    [Fact]
    public async Task Add_time_uses_pulse_count_when_denomination_under_reports()
    {
        await _sessionService.StartSessionAsync(StationId, DeviceType);
        await _sessionService.ApplyCreditAsync(StationId, 600, source: "admin", denomination: null);
        await GrantClaimAsync();

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1,
            Denomination = 5,
            PulseCount = 10
        });

        Assert.True(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.None, outcome.Reason);
        Assert.Equal(3600, outcome.SecondsCredited);
        Assert.Equal(10, _creditPublisher.LastMessage!.Denomination);
        Assert.Equal(10, _eventLogger.Last!.AmountPesos);
        Assert.Contains("\"denomination\":5", _eventLogger.Last.Payload);
        Assert.Contains("\"pulseCount\":10", _eventLogger.Last.Payload);
        Assert.Contains("\"pesoAmount\":10", _eventLogger.Last.Payload);
    }

    [Fact]
    public async Task Pulse_advances_persisted_pulse_seqno_pointer()
    {
        await GrantClaimAsync();

        var before = await _acceptorRepository.GetAsync(AcceptorId);
        Assert.Equal(0, before!.LastProcessedPulseSeqNo);

        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 42,
            Denomination = 20,
            PulseCount = 4
        });

        var after = await _acceptorRepository.GetAsync(AcceptorId);
        Assert.Equal(42, after!.LastProcessedPulseSeqNo);
    }

    [Fact]
    public async Task Second_claim_after_first_end_credits_again()
    {
        // S18a realistic flow: claim1 → coin1 → DONE → claim2 → coin2 → DONE.
        // Both should accumulate, both end-events should unlock with totals.
        var claim1 = await GrantClaimAsync("r-1");
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 1, Denomination = 5, PulseCount = 2
        });
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 2, Denomination = 1, PulseCount = 1
        });
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 3, Denomination = 1, PulseCount = 1
        });
        await _claimService.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-1", ClaimId = claim1.ClaimId, StationId = StationId, EndedAt = FormatUtcNow()
        });

        var claim2 = await GrantClaimAsync("r-2");
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 4, Denomination = 5, PulseCount = 2
        });
        await _claimService.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-2", ClaimId = claim2.ClaimId, StationId = StationId, EndedAt = FormatUtcNow()
        });

        Assert.Equal(4, _creditPublisher.PublishCount);
        // First claim is locked starter time; second claim is active-session add time.
        Assert.Equal(3600, _sessionPublisher.LastState!.RemainingSecondsAtAnchor);
    }

    // ─────────────────────────────────────────────────────────────────
    // E5: NVS-reset realignment via large backward-jump detection.
    // A flash-erase / NVS reset restarts the box counter near 1 while the
    // dedup pointer is far ahead, producing a backward jump a legitimate
    // replay (bounded by the 64-slot NVS buffer) can't. Documented in
    // failure-mode-audit-s28.md gap E5.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E5_large_backward_jump_realigns_and_credits()
    {
        // Field case 2026-06-11: pointer was 811 after a long deployment, the
        // box was reflashed (NVS erased), and the first coin observed in a
        // claim window was seqNo=5. The old seqno==1 + uptime<60s heuristic
        // missed it and every coin was silently dropped.
        await GrantClaimAsync();
        await _acceptorRepository.TryAdvancePulseSeqNoAsync(AcceptorId, 811);
        var creditsBefore = _creditPublisher.PublishCount;

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 5,
            Denomination = 10,
            PulseCount = 3
        });

        Assert.True(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.None, outcome.Reason);
        Assert.Equal(creditsBefore + 1, _creditPublisher.PublishCount);

        // Pointer realigned to incoming-1 (4) then advanced to the incoming 5.
        var acceptor = await _acceptorRepository.GetAsync(AcceptorId);
        Assert.NotNull(acceptor);
        Assert.Equal(5, acceptor!.LastProcessedPulseSeqNo);

        // Audit row carries the old/new pointer + the jump magnitude.
        var resetRow = _eventLogger.Appended.SingleOrDefault(
            e => e.EventType == EventTypes.AcceptorSeqNoReset);
        Assert.NotNull(resetRow);
        Assert.Equal(AcceptorId, resetRow!.AcceptorId);
        Assert.Null(resetRow.StationId);
        using var doc = JsonDocument.Parse(resetRow.Payload);
        Assert.Equal(811, doc.RootElement.GetProperty("oldPointer").GetInt64());
        Assert.Equal(4, doc.RootElement.GetProperty("newPointer").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("triggerSeqNo").GetInt64());
        Assert.Equal(806, doc.RootElement.GetProperty("backwardJump").GetInt64());
    }

    [Fact]
    public async Task E5_exact_duplicate_stays_deduped()
    {
        // A QoS-1 redelivery of an already-processed coin: same seqNo as the
        // pointer (backwardJump = 0). Must not realign, must not re-credit.
        await GrantClaimAsync();
        await _acceptorRepository.TryAdvancePulseSeqNoAsync(AcceptorId, 200);
        var creditsBefore = _creditPublisher.PublishCount;

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 200,
            Denomination = 10,
            PulseCount = 3
        });

        Assert.False(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.DuplicateOrStaleSeqNo, outcome.Reason);
        Assert.Equal(creditsBefore, _creditPublisher.PublishCount);
        Assert.Equal(200, (await _acceptorRepository.GetAsync(AcceptorId))!.LastProcessedPulseSeqNo);
        Assert.DoesNotContain(_eventLogger.Appended,
            e => e.EventType == EventTypes.AcceptorSeqNoReset);
    }

    [Fact]
    public async Task E5_replay_within_nvs_buffer_stays_deduped()
    {
        // Reconnect replay: the ESP32's 64-slot NVS buffer replays older
        // unACKed pulses in order; some were already processed. A backward
        // jump within the buffer depth (here exactly 64) is a legitimate
        // out-of-order replay — below the reset threshold — so it stays
        // deduped rather than being mistaken for a reset.
        await GrantClaimAsync();
        await _acceptorRepository.TryAdvancePulseSeqNoAsync(AcceptorId, 70);
        var creditsBefore = _creditPublisher.PublishCount;

        var outcome = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 6, // 70 - 6 = 64 backward, still a legitimate replay
            Denomination = 10,
            PulseCount = 3
        });

        Assert.False(outcome.Credited);
        Assert.Equal(PulseService.RejectReason.DuplicateOrStaleSeqNo, outcome.Reason);
        Assert.Equal(creditsBefore, _creditPublisher.PublishCount);
        Assert.Equal(70, (await _acceptorRepository.GetAsync(AcceptorId))!.LastProcessedPulseSeqNo);
        Assert.DoesNotContain(_eventLogger.Appended,
            e => e.EventType == EventTypes.AcceptorSeqNoReset);
    }

    [Fact]
    public async Task E5_threshold_boundary_realigns_at_100_not_at_99()
    {
        // Pin the constant: a backward jump of exactly 100 realigns; 99 does not.
        await GrantClaimAsync();

        // 99 case — pointer 104, incoming 5 → no realign, stays deduped.
        await _acceptorRepository.TryAdvancePulseSeqNoAsync(AcceptorId, 104);
        var creditsBefore = _creditPublisher.PublishCount;

        var below = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 5, Denomination = 10, PulseCount = 3
        });

        Assert.False(below.Credited);
        Assert.Equal(PulseService.RejectReason.DuplicateOrStaleSeqNo, below.Reason);
        Assert.Equal(creditsBefore, _creditPublisher.PublishCount);
        Assert.Equal(104, (await _acceptorRepository.GetAsync(AcceptorId))!.LastProcessedPulseSeqNo);

        // 100 case — pointer 105, incoming 5 → realign + credit.
        await _acceptorRepository.TryAdvancePulseSeqNoAsync(AcceptorId, 105);

        var at = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 5, Denomination = 10, PulseCount = 3
        });

        Assert.True(at.Credited);
        Assert.Equal(creditsBefore + 1, _creditPublisher.PublishCount);
        Assert.Equal(5, (await _acceptorRepository.GetAsync(AcceptorId))!.LastProcessedPulseSeqNo);
    }

    [Fact]
    public async Task E5_realign_keeps_stale_replay_below_trigger_deduped()
    {
        // After a reset realign to (incoming-1), a late QoS-1 replay carrying
        // a seqNo BELOW the trigger must still be deduped — proving we realign
        // to incoming-1, not 0. Otherwise an old replay would re-credit.
        await GrantClaimAsync();
        await _acceptorRepository.TryAdvancePulseSeqNoAsync(AcceptorId, 811);

        // Reset detected on seqNo=5 → pointer realigns to 4, advances to 5.
        await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 5, Denomination = 10, PulseCount = 3
        });
        Assert.Equal(5, (await _acceptorRepository.GetAsync(AcceptorId))!.LastProcessedPulseSeqNo);

        var creditsAfterReset = _creditPublisher.PublishCount;

        // A stale replay of seqNo=3 (below the trigger 5): backwardJump = 5-3 = 2,
        // far under the threshold → stays deduped, no re-credit.
        var stale = await _pulseService.ProcessPulseAsync(AcceptorId, new PulseMessage
        {
            SeqNo = 3, Denomination = 20, PulseCount = 4
        });

        Assert.False(stale.Credited);
        Assert.Equal(PulseService.RejectReason.DuplicateOrStaleSeqNo, stale.Reason);
        Assert.Equal(creditsAfterReset, _creditPublisher.PublishCount);
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
