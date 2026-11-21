using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using RentalServer.Core.Abuse;
using RentalServer.Core.Acceptors;
using RentalServer.Core.EventLog;
using RentalShared.Mqtt;

namespace RentalServer.Core.Claims;

public sealed class ClaimService
{
    // 60 s multi-coin window. autoOffSec = window + 5 (ESP32 safety net).
    public const int DefaultClaimWindowSeconds = 60;
    public const int DefaultLedAutoOffSeconds = 65;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ClaimRepository _claimRepository;
    private readonly AcceptorRepository _acceptorRepository;
    private readonly IClaimResultPublisher _claimResultPublisher;
    private readonly ILedPublisher _ledPublisher;
    private readonly Func<string, CancellationToken, Task<ClaimRateMode>> _resolveRateModeAsync;
    private readonly Func<ClaimRecord, int, CancellationToken, Task> _applyResolvedCreditAsync;
    private readonly IEventLogger? _eventLogger;
    private readonly int _claimWindowSeconds;
    private readonly int _ledAutoOffSeconds;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ttlTokens = new();
    private readonly AbusePreventionService? _abusePrevention;

    public ClaimService(
        ClaimRepository claimRepository,
        AcceptorRepository acceptorRepository,
        IClaimResultPublisher claimResultPublisher,
        ILedPublisher ledPublisher,
        int claimWindowSeconds = DefaultClaimWindowSeconds,
        int ledAutoOffSeconds = DefaultLedAutoOffSeconds,
        Func<string, CancellationToken, Task<ClaimRateMode>>? resolveRateModeAsync = null,
        Func<ClaimRecord, int, CancellationToken, Task>? applyResolvedCreditAsync = null,
        IEventLogger? eventLogger = null,
        AbusePreventionService? abusePrevention = null)
    {
        _claimRepository = claimRepository;
        _acceptorRepository = acceptorRepository;
        _claimResultPublisher = claimResultPublisher;
        _ledPublisher = ledPublisher;
        _resolveRateModeAsync = resolveRateModeAsync
                                ?? ((_, _) => Task.FromResult(ClaimRateMode.InsertCoin));
        _applyResolvedCreditAsync = applyResolvedCreditAsync
                                    ?? ((_, _, _) => Task.CompletedTask);
        _eventLogger = eventLogger;
        _claimWindowSeconds = claimWindowSeconds;
        _ledAutoOffSeconds = ledAutoOffSeconds;
        _abusePrevention = abusePrevention;
    }

    public int ClaimWindowSeconds => _claimWindowSeconds;
    public int LedAutoOffSeconds => _ledAutoOffSeconds;

    public event EventHandler<ClaimRecord>? ClaimGranted;
    public event EventHandler<ClaimRecord>? ClaimResolved;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _claimRepository.InitializeAsync(cancellationToken);
        await _claimRepository.ExpireAllPendingAsync(
            FormatUtc(DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public async Task HandleClaimRequestAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!MqttTopics.TryGetStationIdFromClaimRequestTopic(topic, out var stationId))
        {
            return;
        }

        var request = JsonSerializer.Deserialize<ClaimRequestMessage>(payload, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        await ProcessClaimRequestAsync(stationId, request, cancellationToken);
    }

    public async Task ProcessClaimRequestAsync(
        string stationId,
        ClaimRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        if (_abusePrevention is not null)
        {
            var ban = _abusePrevention.CheckBan(stationId);
            if (ban.IsBanned)
            {
                await _claimResultPublisher.PublishClaimResultAsync(
                    stationId,
                    new ClaimResultMessage
                    {
                        RequestId = request.RequestId,
                        Result = "denied",
                        Reason = "abuse_ban",
                        BanUntil = ban.BanUntilUtc
                    },
                    cancellationToken);
                return;
            }
        }

        var acceptor = await _acceptorRepository.GetFirstOnlineAsync(cancellationToken);
        if (acceptor is null)
        {
            await DenyAsync(stationId, request.RequestId, "no_acceptor_online", heldBy: null, cancellationToken);
            return;
        }

        var nowUtc = FormatUtc(DateTimeOffset.UtcNow);
        var expiresUtc = FormatUtc(DateTimeOffset.UtcNow.AddSeconds(_claimWindowSeconds));
        var rateMode = await _resolveRateModeAsync(stationId, cancellationToken);
        var claim = new ClaimRecord(
            ClaimId: GenerateClaimId(),
            RequestId: request.RequestId,
            StationId: stationId,
            AcceptorId: acceptor.AcceptorId,
            GrantedAt: nowUtc,
            ExpiresAt: expiresUtc,
            ResolvedAt: null,
            Status: ClaimStatus.Pending,
            RateMode: rateMode);

        try
        {
            var granted = await _claimRepository.TryInsertPendingAsync(claim, cancellationToken);
            if (!granted)
            {
                var current = await _claimRepository.GetPendingForAcceptorAsync(
                    acceptor.AcceptorId,
                    cancellationToken);
                await DenyAsync(stationId, request.RequestId, "slot_in_use", current?.StationId, cancellationToken);
                return;
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteExtendedErrorCode == 787)
        {
            await DenyAsync(stationId, request.RequestId, "station_unknown", heldBy: null, cancellationToken);
            return;
        }

        await _claimResultPublisher.PublishClaimResultAsync(
            stationId,
            new ClaimResultMessage
            {
                RequestId = request.RequestId,
                Result = "granted",
                ClaimId = claim.ClaimId,
                WindowSec = _claimWindowSeconds,
                AcceptorId = acceptor.AcceptorId
            },
            cancellationToken);

        await _ledPublisher.PublishLedAsync(
            acceptor.AcceptorId,
            new LedCommandMessage
            {
                State = "on",
                AutoOffSec = _ledAutoOffSeconds,
                ClaimId = claim.ClaimId
            },
            cancellationToken);

        await TryLogClaimGrantedAsync(claim, cancellationToken);

        ClaimGranted?.Invoke(this, claim);

        _ = WatchClaimTtlAsync(claim, cancellationToken);
    }

    // Records the running total while the claim is still pending.
    public Task<bool> TryAccumulateCreditAsync(
        string claimId,
        int secondsToAdd,
        CancellationToken cancellationToken = default)
    {
        return _claimRepository.TryAddAccumulatedCreditsAsync(claimId, secondsToAdd, cancellationToken);
    }

    public Task<bool> TryAccumulatePesosAsync(
        string claimId,
        int pesos,
        CancellationToken cancellationToken = default)
    {
        return _claimRepository.TryAddAccumulatedPesosAsync(claimId, pesos, cancellationToken);
    }

    public async Task ExtendClaimWindowAsync(
        string claimId,
        CancellationToken cancellationToken = default)
    {
        var newExpiresAt = FormatUtc(DateTimeOffset.UtcNow.AddSeconds(_claimWindowSeconds));
        await _claimRepository.ExtendExpiryAsync(claimId, newExpiresAt, cancellationToken);

        if (_ttlTokens.TryRemove(claimId, out var oldCts))
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }

        var claim = await _claimRepository.GetByIdAsync(claimId, cancellationToken);
        if (claim is { Status: ClaimStatus.Pending })
        {
            _ = WatchClaimTtlAsync(claim, cancellationToken);

            // Re-publish the granted claim/result with a fresh windowSec so the
            // client re-anchors its countdown to the full 60 s from now.
            await _claimResultPublisher.PublishClaimResultAsync(
                claim.StationId,
                new ClaimResultMessage
                {
                    RequestId = claim.RequestId,
                    Result = "granted",
                    ClaimId = claim.ClaimId,
                    WindowSec = _claimWindowSeconds,
                    AcceptorId = claim.AcceptorId
                },
                cancellationToken);
        }
    }

    public void NotifyCoinDropped(string stationId)
    {
        _abusePrevention?.ResetOnCoin(stationId);
    }

    public async Task HandleClaimEndAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!MqttTopics.TryGetStationIdFromClaimEndTopic(topic, out var stationId))
        {
            return;
        }

        var message = JsonSerializer.Deserialize<ClaimEndMessage>(payload, JsonOptions);
        if (message is null || string.IsNullOrWhiteSpace(message.ClaimId))
        {
            return;
        }

        await ProcessClaimEndAsync(stationId, message, cancellationToken);
    }

    public async Task ProcessClaimEndAsync(
        string stationId,
        ClaimEndMessage message,
        CancellationToken cancellationToken = default)
    {
        var claim = await _claimRepository.GetByIdAsync(message.ClaimId, cancellationToken);
        if (claim is null)
        {
            return;
        }

        if (!string.Equals(claim.StationId, stationId, StringComparison.Ordinal))
        {
            return;
        }

        if (claim.Status != ClaimStatus.Pending)
        {
            return;
        }

        var accumulated = await _claimRepository.GetAccumulatedCreditsAsync(claim.ClaimId, cancellationToken);
        var terminalStatus = accumulated > 0 ? ClaimStatus.Fulfilled : ClaimStatus.Cancelled;

        var resolved = await ResolveAndApplyIfNeededAsync(
            claim,
            terminalStatus,
            accumulated,
            cancellationToken);

        if (!resolved)
        {
            return;
        }

        if (accumulated == 0)
        {
            _abusePrevention?.RecordEmptyClaim(stationId);
        }

        await _ledPublisher.PublishLedAsync(
            claim.AcceptorId,
            new LedCommandMessage { State = "off", ClaimId = claim.ClaimId },
            cancellationToken);

        ClaimResolved?.Invoke(this, claim with { Status = terminalStatus });
    }

    public Task<ClaimRecord?> GetPendingForAcceptorAsync(
        string acceptorId,
        CancellationToken cancellationToken = default)
    {
        return _claimRepository.GetPendingForAcceptorAsync(acceptorId, cancellationToken);
    }

    private async Task WatchClaimTtlAsync(ClaimRecord claim, CancellationToken externalCancellation)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        _ttlTokens[claim.ClaimId] = cts;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_claimWindowSeconds), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _ttlTokens.TryRemove(claim.ClaimId, out _);
            try { cts.Dispose(); } catch (ObjectDisposedException) { }
        }

        int accumulated;
        try
        {
            accumulated = await _claimRepository.GetAccumulatedCreditsAsync(claim.ClaimId, externalCancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var terminalStatus = accumulated > 0 ? ClaimStatus.Fulfilled : ClaimStatus.Expired;
        var resolved = await ResolveAndApplyIfNeededAsync(
            claim,
            terminalStatus,
            accumulated,
            externalCancellation);

        if (!resolved)
        {
            return;
        }

        if (accumulated == 0)
        {
            _abusePrevention?.RecordEmptyClaim(claim.StationId);
        }

        try
        {
            await _ledPublisher.PublishLedAsync(
                claim.AcceptorId,
                new LedCommandMessage
                {
                    State = "off",
                    ClaimId = claim.ClaimId
                },
                externalCancellation);
        }
        catch (OperationCanceledException)
        {
        }

        ClaimResolved?.Invoke(this, claim with { Status = terminalStatus });
    }

    private async Task<bool> ResolveAndApplyIfNeededAsync(
        ClaimRecord claim,
        ClaimStatus terminalStatus,
        int accumulatedSeconds,
        CancellationToken cancellationToken)
    {
        var resolved = await _claimRepository.TryResolvePendingAsync(
            claim.ClaimId,
            terminalStatus,
            FormatUtc(DateTimeOffset.UtcNow),
            cancellationToken);

        if (!resolved)
        {
            return false;
        }

        if (claim.RateMode == ClaimRateMode.InsertCoin && accumulatedSeconds > 0)
        {
            await _applyResolvedCreditAsync(claim, accumulatedSeconds, cancellationToken);
        }

        await TryLogClaimResolvedAsync(claim, terminalStatus, accumulatedSeconds, cancellationToken);

        return true;
    }

    private async Task TryLogClaimGrantedAsync(
        ClaimRecord claim,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                claimId = claim.ClaimId,
                requestId = claim.RequestId,
                rateMode = claim.RateMode == ClaimRateMode.AddTime ? "add_time" : "insert_coin",
                windowSec = _claimWindowSeconds,
                expiresAt = claim.ExpiresAt
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.ClaimGranted,
                StationId: claim.StationId,
                AcceptorId: claim.AcceptorId,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    private async Task TryLogClaimResolvedAsync(
        ClaimRecord claim,
        ClaimStatus terminalStatus,
        int accumulatedSeconds,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        // The event_type discriminates fulfilled (DONE / TTL with credits),
        // cancelled (DONE with no credits), and expired (TTL with no credits)
        // so reports can compute "user gave up" vs "user committed but didn't
        // pay enough" separately without parsing the payload.
        var eventType = terminalStatus switch
        {
            ClaimStatus.Fulfilled => EventTypes.ClaimEnded,
            ClaimStatus.Cancelled => EventTypes.ClaimCancelled,
            ClaimStatus.Expired => EventTypes.ClaimExpired,
            _ => EventTypes.ClaimEnded
        };

        var accumulatedPesos = await _claimRepository.GetAccumulatedPesosAsync(
            claim.ClaimId,
            cancellationToken);

        var payload = JsonSerializer.Serialize(
            new
            {
                claimId = claim.ClaimId,
                requestId = claim.RequestId,
                rateMode = claim.RateMode == ClaimRateMode.AddTime ? "add_time" : "insert_coin",
                terminalStatus = terminalStatus.ToString().ToLowerInvariant(),
                accumulatedSeconds,
                accumulatedPesos
            },
            JsonOptions);

        // accumulatedSeconds is 0 for cancelled/expired so the column is null
        // in those rows — readers can rely on "non-null seconds_credited means
        // money landed".
        int? secondsCredited = accumulatedSeconds > 0 ? accumulatedSeconds : null;
        int? amountPesos =
            claim.RateMode == ClaimRateMode.InsertCoin && accumulatedSeconds > 0 && accumulatedPesos > 0
                ? accumulatedPesos
                : null;

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: eventType,
                StationId: claim.StationId,
                AcceptorId: claim.AcceptorId,
                AmountPesos: amountPesos,
                SecondsCredited: secondsCredited,
                Payload: payload),
            cancellationToken);
    }

    private Task DenyAsync(
        string stationId,
        string requestId,
        string reason,
        string? heldBy,
        CancellationToken cancellationToken)
    {
        return _claimResultPublisher.PublishClaimResultAsync(
            stationId,
            new ClaimResultMessage
            {
                RequestId = requestId,
                Result = "denied",
                Reason = reason,
                HeldBy = heldBy
            },
            cancellationToken);
    }

    private static string GenerateClaimId()
    {
        return $"c-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
