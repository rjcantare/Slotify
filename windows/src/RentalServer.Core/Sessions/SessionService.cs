using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using RentalServer.Core.Credits;
using RentalServer.Core.EventLog;
using RentalServer.Core.Time;
using RentalShared.Mqtt;

namespace RentalServer.Core.Sessions;

public sealed class SessionService
{
    // ACK timeout per MQTT Protocol §"seqNo / ACK Contract": "Publisher waits
    // 5 seconds for the application-level ACK. If absent, republish."
    private const int CreditAckTimeoutSeconds = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly SessionRepository _repository;
    private readonly ISessionPublisher _sessionPublisher;
    private readonly ICreditPublisher _creditPublisher;
    private readonly IMonotonicClock _clock;
    private readonly IEventLogger? _eventLogger;
    private readonly PendingCreditsRepository? _pendingCredits;

    private readonly ConcurrentDictionary<string, SessionState> _byStation = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SessionService(
        SessionRepository repository,
        ISessionPublisher sessionPublisher,
        ICreditPublisher creditPublisher,
        IMonotonicClock clock,
        IEventLogger? eventLogger = null,
        PendingCreditsRepository? pendingCredits = null)
    {
        _repository = repository;
        _sessionPublisher = sessionPublisher;
        _creditPublisher = creditPublisher;
        _clock = clock;
        _eventLogger = eventLogger;
        _pendingCredits = pendingCredits;
    }

    public event EventHandler<SessionState>? SessionChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _repository.InitializeAsync(cancellationToken);
    }

    /// <summary>
    /// Hydrates the in-memory <c>_byStation</c> cache from the SQLite
    /// <c>sessions</c> table. Called by the host (RentalServer MainWindow)
    /// once after <see cref="InitializeAsync"/> + StationRegistry are ready.
    /// Without this preload, <see cref="GetLiveRemainingSeconds"/> returns
    /// null for any station whose session hasn't been touched since process
    /// start — leaving the Time Remaining column blank until the first
    /// claim/admin action.
    ///
    /// Wall-clock catch-up is applied per row: the persisted remaining is
    /// reduced by (now − updatedAt) so a session that expired while the
    /// server was down comes back as locked-at-zero. Expired rows are persisted
    /// and published immediately so retained MQTT state cannot stay stale.
    ///
    /// <paramref name="deviceTypeByStation"/> must map every active
    /// station_id → device_type (the sessions table has no device_type
    /// column; it lives on stations). Stations missing from the map are
    /// skipped (treated as orphan sessions for retired stations).
    /// </summary>
    public async Task PreloadActiveSessionsAsync(
        IReadOnlyDictionary<string, string> deviceTypeByStation,
        CancellationToken cancellationToken = default)
    {
        var active = await _repository.LoadActiveAsync(cancellationToken);
        var nowWall = DateTimeOffset.UtcNow;
        var nowUtc = FormatUtc(nowWall);
        var nowTicks = _clock.GetTimestamp();
        var expiredTransitions = new List<SessionState>();

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var record in active)
            {
                if (!deviceTypeByStation.TryGetValue(record.StationId, out var deviceType))
                {
                    continue;
                }

                int adjustedRemaining;
                try
                {
                    var updated = DateTimeOffset.Parse(
                        record.UpdatedAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    var elapsed = (nowWall - updated).TotalSeconds;
                    adjustedRemaining = elapsed > 0
                        ? Math.Max(0, record.RemainingSeconds - (int)elapsed)
                        : record.RemainingSeconds;
                }
                catch (FormatException)
                {
                    // Malformed updatedAt — best-effort: trust the persisted
                    // remaining and let the tick loop catch any drift.
                    adjustedRemaining = record.RemainingSeconds;
                }

                var isLockedNow = record.IsLocked || adjustedRemaining == 0;
                var expiredDuringDowntime = !record.IsLocked && adjustedRemaining == 0;

                var state = new SessionState(
                    SessionId: record.SessionId,
                    StationId: record.StationId,
                    DeviceType: deviceType,
                    StartedAt: record.StartedAt,
                    RemainingSecondsAtAnchor: adjustedRemaining,
                    AnchorTimestamp: nowTicks,
                    IsLocked: isLockedNow,
                    LastCreditSeqNo: record.LastCreditSeqNo,
                    UpdatedAt: expiredDuringDowntime ? nowUtc : record.UpdatedAt);

                _byStation[record.StationId] = state;
                if (expiredDuringDowntime)
                {
                    expiredTransitions.Add(state);
                }
            }

            foreach (var locked in expiredTransitions)
            {
                await PersistAsync(locked, cancellationToken);
                await PublishSessionAsync(locked, cancellationToken);
                await TryLogSessionLockedAsync(locked, cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionState> StartSessionAsync(
        string stationId,
        string deviceType,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_byStation.TryGetValue(stationId, out var existing))
            {
                return existing;
            }

            var nowTicks = _clock.GetTimestamp();

            // Adopt an existing active row after a server restart. The monotonic
            // anchor is local-process state, so we re-anchor it to "now" while
            // preserving the persisted remaining time and credit seqno.
            var existingRow = await _repository.GetActiveForStationAsync(stationId, cancellationToken);
            if (existingRow is not null)
            {
                var adopted = new SessionState(
                    SessionId: existingRow.SessionId,
                    StationId: stationId,
                    DeviceType: deviceType,
                    StartedAt: existingRow.StartedAt,
                    RemainingSecondsAtAnchor: existingRow.RemainingSeconds,
                    AnchorTimestamp: nowTicks,
                    IsLocked: existingRow.IsLocked,
                    LastCreditSeqNo: existingRow.LastCreditSeqNo,
                    UpdatedAt: existingRow.UpdatedAt);
                _byStation[stationId] = adopted;
                return adopted;
            }

            var nowUtc = FormatUtc(DateTimeOffset.UtcNow);
            var state = new SessionState(
                SessionId: GenerateSessionId(),
                StationId: stationId,
                DeviceType: deviceType,
                StartedAt: nowUtc,
                RemainingSecondsAtAnchor: 0,
                AnchorTimestamp: nowTicks,
                IsLocked: true,
                LastCreditSeqNo: 0,
                UpdatedAt: nowUtc);

            _byStation[stationId] = state;
            await PersistAsync(state, cancellationToken);
            await PublishSessionAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionState> ApplyCreditAsync(
        string stationId,
        int secondsToAdd,
        string source,
        int? denomination,
        CancellationToken cancellationToken = default)
    {
        if (secondsToAdd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToAdd));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Credit source is required.", nameof(source));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_byStation.TryGetValue(stationId, out var current))
            {
                throw new InvalidOperationException(
                    $"No active session for station '{stationId}'. Call StartSessionAsync first.");
            }

            var nowTicks = _clock.GetTimestamp();
            var live = ComputeLiveRemaining(current, nowTicks);
            var newSeqNo = current.LastCreditSeqNo + 1;
            var issuedAtUtc = FormatUtc(DateTimeOffset.UtcNow);

            var updated = current with
            {
                RemainingSecondsAtAnchor = live + secondsToAdd,
                AnchorTimestamp = nowTicks,
                IsLocked = false,
                LastCreditSeqNo = newSeqNo,
                UpdatedAt = issuedAtUtc
            };

            _byStation[stationId] = updated;
            await PersistAsync(updated, cancellationToken);

            var creditMessage = new CreditMessage
            {
                SeqNo = newSeqNo,
                SecondsToAdd = secondsToAdd,
                Source = source,
                Denomination = denomination,
                IssuedAt = issuedAtUtc
            };
            await _creditPublisher.PublishCreditAsync(stationId, creditMessage, cancellationToken);
            await TrackPendingCreditAsync(stationId, creditMessage, issuedAtUtc, cancellationToken);
            await PublishSessionAsync(updated, cancellationToken);

            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionState> PublishCreditEventOnlyAsync(
        string stationId,
        int secondsToAdd,
        string source,
        int? denomination,
        CancellationToken cancellationToken = default)
    {
        if (secondsToAdd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToAdd));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Credit source is required.", nameof(source));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_byStation.TryGetValue(stationId, out var current))
            {
                throw new InvalidOperationException(
                    $"No active session for station '{stationId}'. Call StartSessionAsync first.");
            }

            var newSeqNo = current.LastCreditSeqNo + 1;
            var issuedAtUtc = FormatUtc(DateTimeOffset.UtcNow);
            var updated = current with
            {
                LastCreditSeqNo = newSeqNo,
                UpdatedAt = issuedAtUtc
            };

            _byStation[stationId] = updated;
            await PersistAsync(updated, cancellationToken);

            var transientCreditMessage = new CreditMessage
            {
                SeqNo = newSeqNo,
                SecondsToAdd = secondsToAdd,
                Source = source,
                Denomination = denomination,
                IssuedAt = issuedAtUtc
            };
            await _creditPublisher.PublishCreditAsync(stationId, transientCreditMessage, cancellationToken);
            await TrackPendingCreditAsync(stationId, transientCreditMessage, issuedAtUtc, cancellationToken);

            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionState> ApplyDeferredCreditAsync(
        string stationId,
        int secondsToAdd,
        CancellationToken cancellationToken = default)
    {
        if (secondsToAdd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToAdd));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_byStation.TryGetValue(stationId, out var current))
            {
                throw new InvalidOperationException(
                    $"No active session for station '{stationId}'. Call StartSessionAsync first.");
            }

            var nowTicks = _clock.GetTimestamp();
            var live = ComputeLiveRemaining(current, nowTicks);
            var updated = current with
            {
                RemainingSecondsAtAnchor = live + secondsToAdd,
                AnchorTimestamp = nowTicks,
                IsLocked = false,
                UpdatedAt = FormatUtc(DateTimeOffset.UtcNow)
            };

            _byStation[stationId] = updated;
            await PersistAsync(updated, cancellationToken);
            await PublishSessionAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<SessionState?> ClearSessionAsync(
        string stationId,
        string deviceType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            throw new ArgumentException("Device type is required.", nameof(deviceType));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var nowTicks = _clock.GetTimestamp();
            if (!_byStation.TryGetValue(stationId, out var current))
            {
                var existingRow = await _repository.GetActiveForStationAsync(stationId, cancellationToken);
                if (existingRow is null)
                {
                    return null;
                }

                current = new SessionState(
                    SessionId: existingRow.SessionId,
                    StationId: stationId,
                    DeviceType: deviceType,
                    StartedAt: existingRow.StartedAt,
                    RemainingSecondsAtAnchor: existingRow.RemainingSeconds,
                    AnchorTimestamp: nowTicks,
                    IsLocked: existingRow.IsLocked,
                    LastCreditSeqNo: existingRow.LastCreditSeqNo,
                    UpdatedAt: existingRow.UpdatedAt);
            }

            var cleared = current with
            {
                RemainingSecondsAtAnchor = 0,
                AnchorTimestamp = nowTicks,
                IsLocked = true,
                UpdatedAt = FormatUtc(DateTimeOffset.UtcNow)
            };

            _byStation[stationId] = cleared;
            await PersistAsync(cleared, cancellationToken);
            await PublishSessionAsync(cleared, cancellationToken);
            // S22 fill-in: ClearSessionAsync skipped the audit log in the original
            // S20.9 commit. Now emits session_cleared so admins can see when a
            // station's time was force-wiped (separate from natural tick-lock).
            await TryLogSessionClearedAsync(cleared, current.RemainingSecondsAtAnchor, cancellationToken);
            return cleared;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task TryLogSessionClearedAsync(
        SessionState clearedState,
        int previousRemainingSec,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                sessionId = clearedState.SessionId,
                deviceType = clearedState.DeviceType,
                clearedAt = clearedState.UpdatedAt,
                previousRemainingSec
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.SessionCleared,
                StationId: clearedState.StationId,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    /// <summary>
    /// Subtracts <paramref name="secondsToRemove"/> from the station's live
    /// remaining time, clamped at 0. If clamping brings the session to 0 the
    /// session re-locks. Does NOT touch lastCreditSeqNo — Remove Time is a
    /// session-state change, not a credit event. Returns the updated session
    /// or null when there's no active session to remove from.
    ///
    /// "actualRemoved" in the audit payload may be smaller than the request
    /// when the session had less time than requested left.
    /// </summary>
    public async Task<RemoveTimeResult?> RemoveTimeAsync(
        string stationId,
        string deviceType,
        int secondsToRemove,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            throw new ArgumentException("Device type is required.", nameof(deviceType));
        }
        if (secondsToRemove <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToRemove));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var nowTicks = _clock.GetTimestamp();
            if (!_byStation.TryGetValue(stationId, out var current))
            {
                var existingRow = await _repository.GetActiveForStationAsync(stationId, cancellationToken);
                if (existingRow is null)
                {
                    return null;
                }

                current = new SessionState(
                    SessionId: existingRow.SessionId,
                    StationId: stationId,
                    DeviceType: deviceType,
                    StartedAt: existingRow.StartedAt,
                    RemainingSecondsAtAnchor: existingRow.RemainingSeconds,
                    AnchorTimestamp: nowTicks,
                    IsLocked: existingRow.IsLocked,
                    LastCreditSeqNo: existingRow.LastCreditSeqNo,
                    UpdatedAt: existingRow.UpdatedAt);
            }

            var liveRemaining = ComputeLiveRemaining(current, nowTicks);
            var newRemaining = Math.Max(0, liveRemaining - secondsToRemove);
            var actualRemoved = liveRemaining - newRemaining;

            // No-op cases: already at zero, or already locked. The caller's
            // result reflects "nothing changed" so the UI can show a status
            // hint without spamming the audit log with empty rows.
            if (actualRemoved == 0)
            {
                return new RemoveTimeResult(current, ActualRemovedSec: 0, NowLocked: current.IsLocked);
            }

            var nowLocked = newRemaining == 0;
            var updated = current with
            {
                RemainingSecondsAtAnchor = newRemaining,
                AnchorTimestamp = nowTicks,
                IsLocked = nowLocked,
                UpdatedAt = FormatUtc(DateTimeOffset.UtcNow)
            };

            _byStation[stationId] = updated;
            await PersistAsync(updated, cancellationToken);
            await PublishSessionAsync(updated, cancellationToken);
            await TryLogTimeRemovedAsync(updated, liveRemaining, actualRemoved, cancellationToken);

            return new RemoveTimeResult(updated, actualRemoved, nowLocked);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<TransferTimeResult?> TransferRemainingAsync(
        string sourceStationId,
        string sourceDeviceType,
        string targetStationId,
        string targetDeviceType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDeviceType))
        {
            throw new ArgumentException("Source device type is required.", nameof(sourceDeviceType));
        }

        if (string.IsNullOrWhiteSpace(targetDeviceType))
        {
            throw new ArgumentException("Target device type is required.", nameof(targetDeviceType));
        }

        if (string.Equals(sourceStationId, targetStationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and target station must be different.", nameof(targetStationId));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var nowTicks = _clock.GetTimestamp();
            var source = await GetOrAdoptStateForUpdateAsync(
                sourceStationId,
                sourceDeviceType,
                nowTicks,
                createIfMissing: false,
                cancellationToken);
            if (source is null)
            {
                return null;
            }

            var target = await GetOrAdoptStateForUpdateAsync(
                targetStationId,
                targetDeviceType,
                nowTicks,
                createIfMissing: true,
                cancellationToken);
            if (target is null)
            {
                throw new InvalidOperationException("Target session could not be created.");
            }

            var sourceRemaining = ComputeLiveRemaining(source, nowTicks);
            if (sourceRemaining <= 0)
            {
                return new TransferTimeResult(source, target, SecondsTransferred: 0);
            }

            var targetRemaining = ComputeLiveRemaining(target, nowTicks);
            if (targetRemaining > 0)
            {
                throw new InvalidOperationException("Target station already has active time.");
            }

            var nowUtc = FormatUtc(DateTimeOffset.UtcNow);
            var sourceUpdated = source with
            {
                RemainingSecondsAtAnchor = 0,
                AnchorTimestamp = nowTicks,
                IsLocked = true,
                UpdatedAt = nowUtc
            };
            var targetUpdated = target with
            {
                RemainingSecondsAtAnchor = sourceRemaining,
                AnchorTimestamp = nowTicks,
                IsLocked = false,
                UpdatedAt = nowUtc
            };

            _byStation[sourceStationId] = sourceUpdated;
            _byStation[targetStationId] = targetUpdated;

            await PersistAsync(sourceUpdated, cancellationToken);
            await PersistAsync(targetUpdated, cancellationToken);
            await PublishSessionAsync(sourceUpdated, cancellationToken);
            await PublishSessionAsync(targetUpdated, cancellationToken);

            return new TransferTimeResult(sourceUpdated, targetUpdated, sourceRemaining);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SessionState?> GetOrAdoptStateForUpdateAsync(
        string stationId,
        string deviceType,
        long nowTicks,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (_byStation.TryGetValue(stationId, out var current))
        {
            return current;
        }

        var existingRow = await _repository.GetActiveForStationAsync(stationId, cancellationToken);
        if (existingRow is not null)
        {
            var adopted = new SessionState(
                SessionId: existingRow.SessionId,
                StationId: stationId,
                DeviceType: deviceType,
                StartedAt: existingRow.StartedAt,
                RemainingSecondsAtAnchor: existingRow.RemainingSeconds,
                AnchorTimestamp: nowTicks,
                IsLocked: existingRow.IsLocked,
                LastCreditSeqNo: existingRow.LastCreditSeqNo,
                UpdatedAt: existingRow.UpdatedAt);
            _byStation[stationId] = adopted;
            return adopted;
        }

        if (!createIfMissing)
        {
            return null;
        }

        var nowUtc = FormatUtc(DateTimeOffset.UtcNow);
        var created = new SessionState(
            SessionId: GenerateSessionId(),
            StationId: stationId,
            DeviceType: deviceType,
            StartedAt: nowUtc,
            RemainingSecondsAtAnchor: 0,
            AnchorTimestamp: nowTicks,
            IsLocked: true,
            LastCreditSeqNo: 0,
            UpdatedAt: nowUtc);
        _byStation[stationId] = created;
        return created;
    }

    private async Task TryLogTimeRemovedAsync(
        SessionState state,
        int previousRemainingSec,
        int actualRemovedSec,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                sessionId = state.SessionId,
                deviceType = state.DeviceType,
                removedAt = state.UpdatedAt,
                previousRemainingSec,
                newRemainingSec = state.RemainingSecondsAtAnchor,
                actualRemovedSec
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.AdminRemoveTime,
                StationId: state.StationId,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: actualRemovedSec, // positive value, paired with the AdminRemoveTime event_type
                Payload: payload),
            cancellationToken);
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var nowTicks = _clock.GetTimestamp();
            var transitioned = new List<SessionState>();

            foreach (var (stationId, state) in _byStation)
            {
                if (state.IsLocked)
                {
                    continue;
                }

                if (ComputeLiveRemaining(state, nowTicks) > 0)
                {
                    continue;
                }

                var locked = state with
                {
                    RemainingSecondsAtAnchor = 0,
                    AnchorTimestamp = nowTicks,
                    IsLocked = true,
                    UpdatedAt = FormatUtc(DateTimeOffset.UtcNow)
                };
                _byStation[stationId] = locked;
                transitioned.Add(locked);
            }

            foreach (var locked in transitioned)
            {
                await PersistAsync(locked, cancellationToken);
                await PublishSessionAsync(locked, cancellationToken);
                await TryLogSessionLockedAsync(locked, cancellationToken);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        // Retry sweep runs OUTSIDE the writeGate so a slow republish (e.g. broker
        // momentarily unreachable) does not block session lock transitions on
        // other stations. The sweep is read-mostly on the SQLite side and the
        // publish path is independent of session state.
        await RetryStalePendingCreditsAsync(cancellationToken);
    }

    /// <summary>
    /// Republishes any credit whose ACK has been outstanding longer than the
    /// 5-second protocol window. Idempotent on the station side because the
    /// CreditMessage carries the same seqNo and stations dedup against their
    /// own lastProcessedCreditSeqNo. Hands off cleanly when the pending_credits
    /// repository is not configured (e.g. older tests that construct
    /// SessionService without the optional collaborator).
    /// </summary>
    public async Task RetryStalePendingCreditsAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingCredits is null)
        {
            return;
        }

        var staleBefore = DateTimeOffset.UtcNow.AddSeconds(-CreditAckTimeoutSeconds);
        var stale = await _pendingCredits.ListStaleBeforeAsync(
            FormatUtc(staleBefore),
            cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var row in stale)
        {
            CreditMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<CreditMessage>(row.Payload, JsonOptions);
            }
            catch (JsonException)
            {
                // Corrupted row — drop it so the sweep doesn't spin forever on it.
                // Stations recover via the retained session payload anyway.
                await _pendingCredits.DeleteAsync(row.StationId, row.SeqNo, cancellationToken);
                continue;
            }

            if (message is null)
            {
                await _pendingCredits.DeleteAsync(row.StationId, row.SeqNo, cancellationToken);
                continue;
            }

            await _creditPublisher.PublishCreditAsync(row.StationId, message, cancellationToken);
            await _pendingCredits.MarkRetriedAsync(
                row.StationId,
                row.SeqNo,
                FormatUtc(DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }

    /// <summary>
    /// Stores a credit message in the pending_credits queue so the retry
    /// sweep can republish it if the station's credit/ack never arrives.
    /// No-op when pending_credits is not wired (older tests / off-path
    /// deployments). The credit was already published by the caller; this
    /// just records that we are waiting on the ACK.
    /// </summary>
    private async Task TrackPendingCreditAsync(
        string stationId,
        CreditMessage message,
        string issuedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_pendingCredits is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(message, JsonOptions);
        await _pendingCredits.InsertAsync(
            stationId,
            message.SeqNo,
            payload,
            issuedAtUtc,
            cancellationToken);
    }

    private async Task TryLogSessionLockedAsync(SessionState state, CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                sessionId = state.SessionId,
                deviceType = state.DeviceType,
                lockedAt = state.UpdatedAt
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.SessionLocked,
                StationId: state.StationId,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    public int? GetLiveRemainingSeconds(string stationId)
    {
        return _byStation.TryGetValue(stationId, out var state)
            ? ComputeLiveRemaining(state, _clock.GetTimestamp())
            : null;
    }

    public bool TryGetState(string stationId, out SessionState state)
    {
        return _byStation.TryGetValue(stationId, out state!);
    }

    private int ComputeLiveRemaining(SessionState state, long nowTicks)
    {
        if (state.IsLocked)
        {
            return 0;
        }

        var elapsedTicks = nowTicks - state.AnchorTimestamp;
        if (elapsedTicks <= 0)
        {
            return state.RemainingSecondsAtAnchor;
        }

        var elapsedSeconds = (int)(elapsedTicks / _clock.Frequency);
        var remaining = state.RemainingSecondsAtAnchor - elapsedSeconds;
        return remaining > 0 ? remaining : 0;
    }

    private Task PersistAsync(SessionState state, CancellationToken cancellationToken)
    {
        var record = new SessionRecord(
            SessionId: state.SessionId,
            StationId: state.StationId,
            StartedAt: state.StartedAt,
            EndedAt: null,
            RemainingSeconds: state.RemainingSecondsAtAnchor,
            IsLocked: state.IsLocked,
            LastCreditSeqNo: state.LastCreditSeqNo,
            UpdatedAt: state.UpdatedAt);

        return _repository.UpsertAsync(record, cancellationToken);
    }

    private async Task PublishSessionAsync(SessionState state, CancellationToken cancellationToken)
    {
        await _sessionPublisher.PublishSessionAsync(state, cancellationToken);
        SessionChanged?.Invoke(this, state);
    }

    private static string GenerateSessionId()
    {
        return $"s-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
