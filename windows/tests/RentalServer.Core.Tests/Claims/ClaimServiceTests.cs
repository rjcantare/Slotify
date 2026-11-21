using System.Globalization;
using RentalServer.Core.Acceptors;
using RentalServer.Core.Claims;
using RentalServer.Core.EventLog;
using RentalServer.Core.Stations;
using RentalServer.Core.Tests.Acceptors;
using RentalServer.Core.Tests.EventLog;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Claims;

public sealed class ClaimServiceTests : IAsyncLifetime
{
    private const string StationId = "pc-01";
    private const string OtherStationId = "pc-02";
    private const string AcceptorId = "coin-01";
    private const string DeviceType = "pc";

    private string _databasePath = string.Empty;
    private StationRepository _stationRepository = null!;
    private AcceptorRepository _acceptorRepository = null!;
    private ClaimRepository _claimRepository = null!;
    private FakeClaimResultPublisher _claimResultPublisher = null!;
    private FakeLedPublisher _ledPublisher = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");
        _stationRepository = new StationRepository(_databasePath);
        _acceptorRepository = new AcceptorRepository(_databasePath);
        _claimRepository = new ClaimRepository(_databasePath);

        await _stationRepository.InitializeAsync();
        await _acceptorRepository.InitializeAsync();
        await _claimRepository.InitializeAsync();

        await _stationRepository.UpsertOnlineAsync(StationId, DeviceType, "dev", FormatUtcNow());
        await _stationRepository.UpsertOnlineAsync(OtherStationId, DeviceType, "dev", FormatUtcNow());
        await _acceptorRepository.UpsertOnlineAsync(AcceptorId, "fw-1", -50, FormatUtcNow());

        _claimResultPublisher = new FakeClaimResultPublisher();
        _ledPublisher = new FakeLedPublisher();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    private ClaimService CreateService(
        int windowSeconds = 30,
        int autoOffSeconds = 35,
        IEventLogger? eventLogger = null)
    {
        return new ClaimService(
            _claimRepository,
            _acceptorRepository,
            _claimResultPublisher,
            _ledPublisher,
            windowSeconds,
            autoOffSeconds,
            eventLogger: eventLogger);
    }

    [Fact]
    public async Task Grant_when_acceptor_online_and_slot_free()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        Assert.Equal(1, _claimResultPublisher.PublishCount);
        Assert.Equal("granted", _claimResultPublisher.LastMessage!.Result);
        Assert.Equal(30, _claimResultPublisher.LastMessage.WindowSec);
        Assert.Equal(AcceptorId, _claimResultPublisher.LastMessage.AcceptorId);

        Assert.Equal(1, _ledPublisher.PublishCount);
        Assert.Equal("on", _ledPublisher.LastMessage!.State);
        Assert.Equal(35, _ledPublisher.LastMessage.AutoOffSec);

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(pending);
        Assert.Equal(StationId, pending!.StationId);
    }

    [Fact]
    public async Task Deny_no_acceptor_online_when_acceptor_marked_offline()
    {
        await _acceptorRepository.MarkOfflineAsync(AcceptorId, FormatUtcNow());

        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        Assert.Equal("denied", _claimResultPublisher.LastMessage!.Result);
        Assert.Equal("no_acceptor_online", _claimResultPublisher.LastMessage.Reason);
        Assert.Equal(0, _ledPublisher.PublishCount);
    }

    [Fact]
    public async Task Deny_station_unknown_when_station_row_missing()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync("ghost-99", new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = "ghost-99",
            RequestedAt = FormatUtcNow()
        });

        Assert.Equal("denied", _claimResultPublisher.LastMessage!.Result);
        Assert.Equal("station_unknown", _claimResultPublisher.LastMessage.Reason);
    }

    [Fact]
    public async Task Deny_slot_in_use_when_another_station_holds_pending()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        await service.ProcessClaimRequestAsync(OtherStationId, new ClaimRequestMessage
        {
            RequestId = "r-2",
            StationId = OtherStationId,
            RequestedAt = FormatUtcNow()
        });

        var second = _claimResultPublisher.Published[^1].Message;
        Assert.Equal("denied", second.Result);
        Assert.Equal("slot_in_use", second.Reason);
        Assert.Equal(StationId, second.HeldBy);
    }

    [Fact]
    public async Task Simultaneous_claims_only_one_grants()
    {
        var service = CreateService();
        await service.InitializeAsync();

        // Drive both claim requests truly concurrently. The DB's partial unique index
        // on claims(acceptor_id) WHERE status='pending' is the single-writer arbiter;
        // exactly one TryInsertPending must succeed.
        var first = service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });
        var second = service.ProcessClaimRequestAsync(OtherStationId, new ClaimRequestMessage
        {
            RequestId = "r-2",
            StationId = OtherStationId,
            RequestedAt = FormatUtcNow()
        });

        await Task.WhenAll(first, second);

        var grants = _claimResultPublisher.Published.Count(p => p.Message.Result == "granted");
        var denies = _claimResultPublisher.Published.Count(p => p.Message.Result == "denied");
        Assert.Equal(1, grants);
        Assert.Equal(1, denies);
    }

    [Fact]
    public async Task TTL_expires_pending_and_publishes_led_off_after_window()
    {
        // 1-second window to keep the test fast. The TTL task is Task.Delay-backed so
        // this is a real-time wait, but bounded.
        var service = CreateService(windowSeconds: 1, autoOffSeconds: 2);
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        // Wait long enough for the Task.Delay-driven TTL to fire.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.Null(pending);

        // LED commands: "on" at grant + "off" at expiry.
        Assert.Equal(2, _ledPublisher.PublishCount);
        Assert.Equal("off", _ledPublisher.LastMessage!.State);
    }

    // Per-coin credits are applied by PulseService. claim/end only resolves the
    // claim and publishes LED off; it must not add the accumulated total again.
    [Fact]
    public async Task ClaimEnd_with_accumulated_credits_fulfills_without_reapplying_time()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(pending);

        // Simulate two coins landing within the window.
        Assert.True(await service.TryAccumulateCreditAsync(pending!.ClaimId, 1800));
        Assert.True(await service.TryAccumulateCreditAsync(pending.ClaimId, 360));

        await service.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = pending.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        Assert.Null(await _claimRepository.GetPendingForAcceptorAsync(AcceptorId));
        Assert.Equal(2, _ledPublisher.PublishCount); // on + off
        Assert.Equal("off", _ledPublisher.LastMessage!.State);

        Assert.Equal(2160, await _claimRepository.GetAccumulatedCreditsAsync(pending.ClaimId));
    }

    [Fact]
    public async Task ClaimEnd_locked_starter_logs_total_pesos()
    {
        var eventLogger = new RecordingEventLogger();
        var service = CreateService(eventLogger: eventLogger);
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(pending);

        Assert.True(await service.TryAccumulatePesosAsync(pending!.ClaimId, 5));
        Assert.True(await service.TryAccumulateCreditAsync(pending.ClaimId, 1800));
        Assert.True(await service.TryAccumulatePesosAsync(pending.ClaimId, 5));
        Assert.True(await service.TryAccumulateCreditAsync(pending.ClaimId, 1800));

        await service.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = pending.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        var row = eventLogger.Appended.Last(e => e.EventType == EventTypes.ClaimEnded);
        Assert.Equal(10, row.AmountPesos);
        Assert.Equal(3600, row.SecondsCredited);
        Assert.Contains("\"accumulatedPesos\":10", row.Payload);
    }

    [Fact]
    public async Task ClaimEnd_without_credits_cancels_and_skips_unlock()
    {
        // User clicked DONE before dropping any coin: status -> 'cancelled',
        // LED still goes off.
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);

        await service.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = pending!.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        Assert.Null(await _claimRepository.GetPendingForAcceptorAsync(AcceptorId));
        Assert.Equal(2, _ledPublisher.PublishCount);
        Assert.Equal("off", _ledPublisher.LastMessage!.State);
    }

    [Fact]
    public async Task ClaimEnd_with_unknown_claimid_is_noop()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = "c-does-not-exist",
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        Assert.Equal(0, _ledPublisher.PublishCount);
    }

    [Fact]
    public async Task Duplicate_claim_end_is_noop()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        await service.TryAccumulateCreditAsync(pending!.ClaimId, 360);

        var endMessage = new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = pending.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        };

        await service.ProcessClaimEndAsync(StationId, endMessage);
        await service.ProcessClaimEndAsync(StationId, endMessage); // second call: no-op

        // LED on (grant) + LED off (first end). Second end must NOT publish another off.
        Assert.Equal(2, _ledPublisher.PublishCount);
    }

    [Fact]
    public async Task ClaimEnd_with_mismatched_stationid_is_ignored()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);

        // Someone publishes claim/end on pc-02's topic but cites pc-01's claimId. We
        // route by topic stationId; the mismatch with the claim's StationId rejects.
        await service.ProcessClaimEndAsync(OtherStationId, new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = pending!.ClaimId,
            StationId = OtherStationId,
            EndedAt = FormatUtcNow()
        });

        // Claim still pending.
        var stillPending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.NotNull(stillPending);
        Assert.Equal(1, _ledPublisher.PublishCount); // only grant LED on
    }

    [Fact]
    public async Task TTL_with_accumulated_credits_fulfills_without_reapplying_time()
    {
        var service = CreateService(windowSeconds: 1, autoOffSeconds: 2);
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        await service.TryAccumulateCreditAsync(pending!.ClaimId, 750);

        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Null(await _claimRepository.GetPendingForAcceptorAsync(AcceptorId));
        Assert.Equal(2, _ledPublisher.PublishCount);
        Assert.Equal("off", _ledPublisher.LastMessage!.State);

        Assert.Equal(750, await _claimRepository.GetAccumulatedCreditsAsync(pending.ClaimId));
    }

    [Fact]
    public async Task TryAccumulate_on_already_resolved_claim_returns_false()
    {
        var service = CreateService();
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        await service.ProcessClaimEndAsync(StationId, new ClaimEndMessage
        {
            RequestId = "r-1",
            ClaimId = pending!.ClaimId,
            StationId = StationId,
            EndedAt = FormatUtcNow()
        });

        // A pulse arriving after claim/end must NOT accumulate.
        Assert.False(await service.TryAccumulateCreditAsync(pending.ClaimId, 1800));
    }

    [Fact]
    public async Task Initialize_clears_lingering_pending_claims_from_prior_crash()
    {
        // Simulate a server crash: insert a pending row directly, then construct a new
        // ClaimService and Initialize. The pending row must move to 'expired'.
        var orphan = new ClaimRecord(
            ClaimId: "c-orphan",
            RequestId: "r-orphan",
            StationId: StationId,
            AcceptorId: AcceptorId,
            GrantedAt: FormatUtcNow(),
            ExpiresAt: FormatUtcNow(),
            ResolvedAt: null,
            Status: ClaimStatus.Pending);
        Assert.True(await _claimRepository.TryInsertPendingAsync(orphan));

        var service = CreateService();
        await service.InitializeAsync();

        var pending = await _claimRepository.GetPendingForAcceptorAsync(AcceptorId);
        Assert.Null(pending);
    }

    [Fact]
    public async Task After_TTL_expiry_next_request_succeeds()
    {
        // No-queue invariant: after the previous claim expires, the slot is FREE for
        // anyone — including a station that wasn't asking before. There is no auto-
        // grant to a queued requester (we don't keep a queue at all).
        var service = CreateService(windowSeconds: 1, autoOffSeconds: 2);
        await service.InitializeAsync();

        await service.ProcessClaimRequestAsync(StationId, new ClaimRequestMessage
        {
            RequestId = "r-1",
            StationId = StationId,
            RequestedAt = FormatUtcNow()
        });

        await Task.Delay(TimeSpan.FromSeconds(2));

        var claimsAtThisPoint = _claimResultPublisher.Published.Count;

        await service.ProcessClaimRequestAsync(OtherStationId, new ClaimRequestMessage
        {
            RequestId = "r-2",
            StationId = OtherStationId,
            RequestedAt = FormatUtcNow()
        });

        var latest = _claimResultPublisher.Published[^1].Message;
        Assert.Equal("granted", latest.Result);
        Assert.True(_claimResultPublisher.PublishCount > claimsAtThisPoint);
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
