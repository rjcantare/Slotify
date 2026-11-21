using System.Globalization;
using System.Text.Json;
using RentalServer.Core.EventLog;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;

namespace RentalServer.Core.Admin;

public sealed class AdminTimeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly StationRepository _stationRepository;
    private readonly SessionService _sessionService;
    private readonly IEventLogger? _eventLogger;

    public AdminTimeService(
        StationRepository stationRepository,
        SessionService sessionService,
        IEventLogger? eventLogger = null)
    {
        _stationRepository = stationRepository;
        _sessionService = sessionService;
        _eventLogger = eventLogger;
    }

    public async Task<AdminAddTimeResult> AddTimeAsync(
        string stationId,
        int secondsToAdd,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            throw new ArgumentException("Station id is required.", nameof(stationId));
        }

        if (secondsToAdd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToAdd));
        }

        var station = await _stationRepository.GetAsync(stationId, cancellationToken);
        if (station is null)
        {
            return new AdminAddTimeResult(
                AdminAddTimeStatus.StationUnknown,
                stationId,
                secondsToAdd,
                Session: null);
        }

        await _sessionService.StartSessionAsync(
            station.StationId,
            station.DeviceType,
            cancellationToken);

        var session = await _sessionService.ApplyCreditAsync(
            station.StationId,
            secondsToAdd,
            source: "admin",
            denomination: null,
            cancellationToken);

        // Audit row written after the credit lands so a partial failure
        // upstream is not papered over with a row that looks consistent.
        if (_eventLogger is not null)
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    sessionId = session.SessionId,
                    secondsToAdd,
                    source = "admin",
                    creditSeqNo = session.LastCreditSeqNo,
                    deviceType = station.DeviceType
                },
                JsonOptions);

            await _eventLogger.AppendAsync(
                new EventLogAppendRequest(
                    Ts: FormatUtc(DateTimeOffset.UtcNow),
                    EventType: EventTypes.AdminCredit,
                    StationId: station.StationId,
                    AcceptorId: null,
                    AmountPesos: null,
                    SecondsCredited: secondsToAdd,
                    Payload: payload),
                cancellationToken);
        }

        return new AdminAddTimeResult(
            AdminAddTimeStatus.Applied,
            station.StationId,
            secondsToAdd,
            session);
    }

    /// <summary>
    /// Reverse of <see cref="AddTimeAsync"/>. Validates the station + delegates
    /// to <see cref="SessionService.RemoveTimeAsync"/> which clamps at 0 and
    /// emits the AdminRemoveTime audit row. SessionService is also where the
    /// retained session republish happens, so callers don't need to wire
    /// anything extra to make the client UIs update.
    /// </summary>
    public async Task<AdminRemoveTimeResult> RemoveTimeAsync(
        string stationId,
        int secondsToRemove,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            throw new ArgumentException("Station id is required.", nameof(stationId));
        }
        if (secondsToRemove <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToRemove));
        }

        var station = await _stationRepository.GetAsync(stationId, cancellationToken);
        if (station is null)
        {
            return new AdminRemoveTimeResult(
                AdminRemoveTimeStatus.StationUnknown,
                stationId,
                secondsToRemove,
                ActualRemovedSec: 0,
                NowLocked: false,
                Session: null);
        }

        var result = await _sessionService.RemoveTimeAsync(
            station.StationId,
            station.DeviceType,
            secondsToRemove,
            cancellationToken);

        if (result is null)
        {
            return new AdminRemoveTimeResult(
                AdminRemoveTimeStatus.NoActiveSession,
                stationId,
                secondsToRemove,
                ActualRemovedSec: 0,
                NowLocked: false,
                Session: null);
        }

        if (result.ActualRemovedSec == 0)
        {
            return new AdminRemoveTimeResult(
                AdminRemoveTimeStatus.NothingToRemove,
                stationId,
                secondsToRemove,
                ActualRemovedSec: 0,
                NowLocked: result.NowLocked,
                Session: result.Session);
        }

        return new AdminRemoveTimeResult(
            AdminRemoveTimeStatus.Applied,
            stationId,
            secondsToRemove,
            result.ActualRemovedSec,
            result.NowLocked,
            result.Session);
    }

    public async Task<AdminTransferTimeResult> TransferTimeAsync(
        string sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceStationId))
        {
            throw new ArgumentException("Source station id is required.", nameof(sourceStationId));
        }

        if (string.IsNullOrWhiteSpace(targetStationId))
        {
            throw new ArgumentException("Target station id is required.", nameof(targetStationId));
        }

        if (string.Equals(sourceStationId, targetStationId, StringComparison.OrdinalIgnoreCase))
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.SameStation,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        var source = await _stationRepository.GetAsync(sourceStationId, cancellationToken);
        if (source is null)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.SourceUnknown,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        var target = await _stationRepository.GetAsync(targetStationId, cancellationToken);
        if (target is null)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.TargetUnknown,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        if (!source.IsOnline)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.SourceOffline,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        if (!target.IsOnline)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.TargetOffline,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        var sourceRemaining = _sessionService.GetLiveRemainingSeconds(sourceStationId);
        if (sourceRemaining is null or <= 0)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.NoSourceTime,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        var targetRemaining = _sessionService.GetLiveRemainingSeconds(targetStationId);
        if (targetRemaining is > 0)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.TargetHasTime,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                SourceSession: null,
                TargetSession: null);
        }

        var transfer = await _sessionService.TransferRemainingAsync(
            sourceStationId,
            source.DeviceType,
            targetStationId,
            target.DeviceType,
            cancellationToken);

        if (transfer is null || transfer.SecondsTransferred <= 0)
        {
            return new AdminTransferTimeResult(
                AdminTransferTimeStatus.NoSourceTime,
                sourceStationId,
                targetStationId,
                SecondsTransferred: 0,
                transfer?.SourceSession,
                transfer?.TargetSession);
        }

        if (_eventLogger is not null)
        {
            var payload = JsonSerializer.Serialize(
                new
                {
                    sourceStationId,
                    targetStationId,
                    sourceSessionId = transfer.SourceSession.SessionId,
                    targetSessionId = transfer.TargetSession.SessionId,
                    secondsTransferred = transfer.SecondsTransferred,
                    sourceDeviceType = source.DeviceType,
                    targetDeviceType = target.DeviceType
                },
                JsonOptions);

            await _eventLogger.AppendAsync(
                new EventLogAppendRequest(
                    Ts: FormatUtc(DateTimeOffset.UtcNow),
                    EventType: EventTypes.AdminTransferTime,
                    StationId: sourceStationId,
                    AcceptorId: null,
                    AmountPesos: null,
                    SecondsCredited: transfer.SecondsTransferred,
                    Payload: payload),
                cancellationToken);
        }

        return new AdminTransferTimeResult(
            AdminTransferTimeStatus.Applied,
            sourceStationId,
            targetStationId,
            transfer.SecondsTransferred,
            transfer.SourceSession,
            transfer.TargetSession);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }
}
