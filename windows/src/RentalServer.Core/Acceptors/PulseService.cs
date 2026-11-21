using System.Globalization;
using System.Text.Json;
using RentalServer.Core.Claims;
using RentalServer.Core.EventLog;
using RentalServer.Core.Rates;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;
using RentalShared.Mqtt;

namespace RentalServer.Core.Acceptors;

public sealed class PulseService
{
    public enum RejectReason
    {
        None,
        DuplicateOrStaleSeqNo,
        NoActiveClaim,
        UnknownStation,
        UnknownDenomination,
        SessionStartFailed
    }

    public sealed record PulseOutcome(
        long SeqNo,
        bool Credited,
        RejectReason Reason,
        string? StationId,
        int? SecondsCredited);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AcceptorRepository _acceptorRepository;
    private readonly ClaimRepository _claimRepository;
    private readonly StationRepository _stationRepository;
    private readonly RateRepository _rateRepository;
    private readonly SessionService _sessionService;
    private readonly ClaimService _claimService;
    private readonly IPulseAckPublisher _pulseAckPublisher;
    private readonly IEventLogger? _eventLogger;

    public PulseService(
        AcceptorRepository acceptorRepository,
        ClaimRepository claimRepository,
        StationRepository stationRepository,
        RateRepository rateRepository,
        SessionService sessionService,
        ClaimService claimService,
        IPulseAckPublisher pulseAckPublisher,
        IEventLogger? eventLogger = null)
    {
        _acceptorRepository = acceptorRepository;
        _claimRepository = claimRepository;
        _stationRepository = stationRepository;
        _rateRepository = rateRepository;
        _sessionService = sessionService;
        _claimService = claimService;
        _pulseAckPublisher = pulseAckPublisher;
        _eventLogger = eventLogger;
    }

    public event EventHandler<PulseOutcome>? PulseProcessed;

    public async Task HandlePulseAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!MqttTopics.TryGetAcceptorIdFromPulseTopic(topic, out var acceptorId))
        {
            return;
        }

        var message = JsonSerializer.Deserialize<PulseMessage>(payload, JsonOptions);
        if (message is null)
        {
            return;
        }

        await ProcessPulseAsync(acceptorId, message, cancellationToken);
    }

    /// <summary>
    /// E5: NVS-reset detection threshold. When a pulse arrives at or below the
    /// stored dedup pointer, the backward distance (pointer − incoming) separates
    /// two very different events:
    ///
    ///   • A genuine QoS-1 redelivery or reconnect-buffer replay. The ESP32's
    ///     NVS replay buffer is 64 slots deep, so a legitimate backward jump is
    ///     at most ~64. These MUST stay deduped, else the coin double-credits.
    ///
    ///   • An NVS counter reset (flash-erase / NVS corruption). The box restarts
    ///     its counter near 1 while our pointer is far ahead (e.g. 811), so the
    ///     backward jump is in the hundreds and never recovers on its own.
    ///     Without realignment every coin after the reset is silently dropped as
    ///     stale — coins accepted, no credit.
    ///
    /// 100 sits above the 64-slot replay buffer with margin, so a legitimate
    /// replay can't trip it. The earlier heuristic (incoming==1 AND uptime&lt;60s)
    /// missed the real 2026-06-11 incident, where the first post-reset coin seen
    /// in a claim window was seqNo=5 and the box had been up for minutes.
    /// </summary>
    private const long NvsResetBackwardJumpThreshold = 100;

    public async Task<PulseOutcome> ProcessPulseAsync(
        string acceptorId,
        PulseMessage message,
        CancellationToken cancellationToken = default)
    {
        var advanced = await _acceptorRepository.TryAdvancePulseSeqNoAsync(
            acceptorId,
            message.SeqNo,
            cancellationToken);

        if (!advanced)
        {
            // Pulse is at or below the dedup pointer. Distinguish a real
            // NVS-reset (large backward jump → realign + credit) from a normal
            // QoS-1 / reconnect-buffer replay (small backward jump → stay
            // deduped). See NvsResetBackwardJumpThreshold.
            if (await TryRealignNvsResetAsync(acceptorId, message.SeqNo, cancellationToken))
            {
                advanced = await _acceptorRepository.TryAdvancePulseSeqNoAsync(
                    acceptorId,
                    message.SeqNo,
                    cancellationToken);
            }

            if (!advanced)
            {
                await AckAsync(acceptorId, message.SeqNo, cancellationToken);
                return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.DuplicateOrStaleSeqNo, null, null));
            }
        }

        var claim = await _claimRepository.GetPendingForAcceptorAsync(acceptorId, cancellationToken);
        if (claim is null)
        {
            await AckAsync(acceptorId, message.SeqNo, cancellationToken);
            return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.NoActiveClaim, null, null));
        }

        var station = await _stationRepository.GetAsync(claim.StationId, cancellationToken);
        if (station is null)
        {
            await AckAsync(acceptorId, message.SeqNo, cancellationToken);
            return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.UnknownStation, claim.StationId, null));
        }

        var pesoAmount = GetPulsePesoAmount(message);
        if (!IsSupportedCoinDenomination(pesoAmount))
        {
            await AckAsync(acceptorId, message.SeqNo, cancellationToken);
            return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.UnknownDenomination, claim.StationId, null));
        }

        int secondsToCredit;
        try
        {
            await _sessionService.StartSessionAsync(claim.StationId, station.DeviceType, cancellationToken);

            var pesosAccumulated = await _claimService.TryAccumulatePesosAsync(
                claim.ClaimId,
                pesoAmount,
                cancellationToken);

            if (!pesosAccumulated)
            {
                await AckAsync(acceptorId, message.SeqNo, cancellationToken);
                return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.NoActiveClaim, claim.StationId, null));
            }

            // Each coin drop extends the claim window so the customer always
            // has the full 60 s from the most recent coin to drop more.
            await _claimService.ExtendClaimWindowAsync(claim.ClaimId, cancellationToken);

            // Each coin drop resets the abuse prevention counter for this station.
            _claimService.NotifyCoinDropped(claim.StationId);

            var calculated = claim.RateMode == ClaimRateMode.AddTime
                ? await CalculateAddTimeCreditAsync(station.DeviceType, pesoAmount, cancellationToken)
                : await CalculateInsertCoinCreditAsync(claim.ClaimId, station.DeviceType, cancellationToken);

            if (calculated is null)
            {
                await AckAsync(acceptorId, message.SeqNo, cancellationToken);
                return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.UnknownDenomination, claim.StationId, null));
            }

            secondsToCredit = calculated.Value;
            if (claim.RateMode == ClaimRateMode.InsertCoin)
            {
                if (secondsToCredit > 0)
                {
                    var lockedCreditsAccumulated = await _claimService.TryAccumulateCreditAsync(
                        claim.ClaimId,
                        secondsToCredit,
                        cancellationToken);

                    if (!lockedCreditsAccumulated)
                    {
                        await AckAsync(acceptorId, message.SeqNo, cancellationToken);
                        return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.NoActiveClaim, claim.StationId, null));
                    }
                }

                await _sessionService.PublishCreditEventOnlyAsync(
                    claim.StationId,
                    secondsToAdd: secondsToCredit,
                    source: "coin",
                    denomination: pesoAmount,
                    cancellationToken);

                await TryLogCoinCreditAsync(
                    claim,
                    acceptorId,
                    message,
                    pesoAmount,
                    secondsToCredit,
                    cancellationToken);

                await AckAsync(acceptorId, message.SeqNo, cancellationToken);
                return Publish(new PulseOutcome(
                    message.SeqNo,
                    secondsToCredit > 0,
                    RejectReason.None,
                    claim.StationId,
                    secondsToCredit));
            }

            var creditsAccumulated = await _claimService.TryAccumulateCreditAsync(
                claim.ClaimId,
                secondsToCredit,
                cancellationToken);

            if (!creditsAccumulated)
            {
                await AckAsync(acceptorId, message.SeqNo, cancellationToken);
                return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.NoActiveClaim, claim.StationId, null));
            }

            await _sessionService.ApplyCreditAsync(
                claim.StationId,
                secondsToAdd: secondsToCredit,
                source: "coin",
                denomination: pesoAmount,
                cancellationToken);

            await TryLogCoinCreditAsync(
                claim,
                acceptorId,
                message,
                pesoAmount,
                secondsToCredit,
                cancellationToken);
        }
        catch (Exception)
        {
            await AckAsync(acceptorId, message.SeqNo, cancellationToken);
            return Publish(new PulseOutcome(message.SeqNo, false, RejectReason.SessionStartFailed, claim.StationId, null));
        }

        await AckAsync(acceptorId, message.SeqNo, cancellationToken);
        return Publish(new PulseOutcome(message.SeqNo, true, RejectReason.None, claim.StationId, secondsToCredit));
    }

    private async Task TryLogCoinCreditAsync(
        ClaimRecord claim,
        string acceptorId,
        PulseMessage message,
        int pesoAmount,
        int secondsToCredit,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                seqNo = message.SeqNo,
                denomination = message.Denomination,
                pulseCount = message.PulseCount,
                pesoAmount,
                claimId = claim.ClaimId,
                rateMode = claim.RateMode == ClaimRateMode.AddTime ? "add_time" : "insert_coin",
                stationId = claim.StationId
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.CoinCredit,
                StationId: claim.StationId,
                AcceptorId: acceptorId,
                AmountPesos: pesoAmount,
                SecondsCredited: secondsToCredit > 0 ? secondsToCredit : null,
                Payload: payload),
            cancellationToken);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }

    private async Task<int?> CalculateAddTimeCreditAsync(
        string deviceType,
        int totalPesos,
        CancellationToken cancellationToken)
    {
        return await _rateRepository.GetAddTimeSecondsForTotalAsync(deviceType, totalPesos, cancellationToken);
    }

    private async Task<int?> CalculateInsertCoinCreditAsync(
        string claimId,
        string deviceType,
        CancellationToken cancellationToken)
    {
        var totalPesos = await _claimRepository.GetAccumulatedPesosAsync(claimId, cancellationToken);
        var targetSeconds = await _rateRepository.GetInsertCoinSecondsForTotalAsync(
            deviceType,
            totalPesos,
            cancellationToken);

        if (targetSeconds is null)
        {
            return null;
        }

        var alreadyCredited = await _claimRepository.GetAccumulatedCreditsAsync(claimId, cancellationToken);
        return Math.Max(0, targetSeconds.Value - alreadyCredited);
    }

    private Task AckAsync(string acceptorId, long seqNo, CancellationToken cancellationToken)
    {
        return _pulseAckPublisher.PublishPulseAckAsync(acceptorId, seqNo, cancellationToken);
    }

    private PulseOutcome Publish(PulseOutcome outcome)
    {
        PulseProcessed?.Invoke(this, outcome);
        return outcome;
    }

    private static bool IsSupportedCoinDenomination(int denomination)
    {
        return denomination is >= 1 and <= 20;
    }

    private static int GetPulsePesoAmount(PulseMessage message)
    {
        // Current production firmware is calibrated in peso-count mode:
        // pulseCount is the physical peso total. Older firmware also sent a
        // decoded denomination where pulseCount could be lower (e.g. ₱20 = 4
        // pulses). Use the larger value so old packets stay correct while a
        // stale/lower denomination field cannot under-report Coin History.
        return Math.Max(message.Denomination, message.PulseCount);
    }

    /// <summary>
    /// E5 NVS-reset realignment. Fires when the incoming pulse sits a LARGE
    /// distance below the stored dedup pointer (>= NvsResetBackwardJumpThreshold),
    /// which only a counter reset can produce — a legitimate replay stays within
    /// the 64-slot NVS buffer. On detection, realigns the pointer to just below
    /// the incoming seqNo so this pulse (and the post-reset stream that follows)
    /// is admitted, while any stale replay carrying a seqNo BELOW the incoming
    /// one stays deduped. Returns true on a successful realign; the caller then
    /// re-runs TryAdvancePulseSeqNoAsync to admit the pulse.
    /// </summary>
    private async Task<bool> TryRealignNvsResetAsync(
        string acceptorId,
        long incomingSeqNo,
        CancellationToken cancellationToken)
    {
        var acceptor = await _acceptorRepository.GetAsync(acceptorId, cancellationToken);
        if (acceptor is null)
        {
            return false;
        }

        var backwardJump = acceptor.LastProcessedPulseSeqNo - incomingSeqNo;
        if (backwardJump < NvsResetBackwardJumpThreshold)
        {
            // Small backward step: a QoS-1 retry or a reconnect-buffer replay
            // of an already-credited coin. Must stay deduped to avoid
            // double-crediting. Not a reset.
            return false;
        }

        // Realign down to incomingSeqNo - 1 (NOT 0) so the incoming pulse is
        // re-admitted as new, but a stale replay carrying a seqNo below the
        // incoming one stays below the pointer and remains deduped.
        var newPointer = incomingSeqNo - 1;
        var oldPointer = await _acceptorRepository.RealignPulseSeqNoAsync(
            acceptorId,
            newPointer,
            cancellationToken);

        if (oldPointer is null)
        {
            // newPointer wasn't strictly less than the current pointer — can't
            // happen given backwardJump >= threshold > 0, but stay defensive
            // against a concurrent advance racing this realign.
            return false;
        }

        if (_eventLogger is not null)
        {
            await _eventLogger.AppendAsync(
                new EventLogAppendRequest(
                    Ts: FormatUtc(DateTimeOffset.UtcNow),
                    EventType: EventTypes.AcceptorSeqNoReset,
                    StationId: null,
                    AcceptorId: acceptorId,
                    AmountPesos: null,
                    SecondsCredited: null,
                    Payload: BuildSeqNoResetPayload(
                        oldPointer: oldPointer.Value,
                        newPointer: newPointer,
                        triggerSeqNo: incomingSeqNo,
                        backwardJump: backwardJump,
                        uptimeSec: acceptor.LastUptimeSec)),
                cancellationToken);
        }

        return true;
    }

    private static string BuildSeqNoResetPayload(
        long oldPointer,
        long newPointer,
        long triggerSeqNo,
        long backwardJump,
        long? uptimeSec)
    {
        var doc = new
        {
            oldPointer,
            newPointer,
            triggerSeqNo,
            backwardJump,
            uptimeSec,
            reason = "ESP32 NVS reset detected via large backward seqno jump"
        };
        return JsonSerializer.Serialize(doc);
    }
}
