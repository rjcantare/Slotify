using System.Diagnostics;
using System.Globalization;
using RentalShared.Mqtt;

namespace RentalClient.Windows.Sessions;

// Anchored monotonic countdown mirror for the station's session.
//
// On Apply:
//   1. Compute a one-shot wall-clock catch-up against the server's `updatedAt`
//      so we don't undercount time that elapsed during a disconnect (MQTT
//      Protocol §2: "effectiveRemainingSec = max(0, remainingSec - secondsSince(updatedAt))").
//   2. Anchor (RemainingAtAnchor, Stopwatch.GetTimestamp()).
//
// On GetCurrentState:
//   - server-locked => return literal RemainingSec.
//   - unlocked => RemainingAtAnchor − monotonic elapsed since the anchor.
//
// The wall-clock math happens ONCE per Apply, never on the ongoing tick.
// CLAUDE.md Project Conventions: "Use Stopwatch.GetTimestamp() for countdowns;
// never DateTime.UtcNow" — the rule applies to the ongoing tick. The one-shot
// reconnect snap against server-stamped `updatedAt` is wall-clock by design.
//
// lastCreditSeqNo monotonic — strictly older messages rejected; equal accepted
// so a locked-flag transition without a new credit still applies.
public sealed class SessionTracker
{
    private readonly object _gate = new();
    private SessionMessage? _latest;
    private long _anchorTimestamp;
    private long _remainingAtAnchor;

    public event EventHandler<SessionTrackerState>? StateChanged;

    public void Apply(SessionMessage message)
    {
        SessionTrackerState newState;

        lock (_gate)
        {
            if (_latest is not null && message.LastCreditSeqNo < _latest.LastCreditSeqNo)
            {
                return;
            }

            // S20.9.1: defend against broker re-delivering the same retained message
            // (e.g. on resubscribe after a connection bounce). Equal-seqNo "lock flag
            // flipped" transitions are still applied because SessionMessage is a
            // record — structural equality catches them by detecting that Locked or
            // RemainingSec differ.
            if (_latest is not null && message == _latest)
            {
                return;
            }

            _latest = message;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _remainingAtAnchor = ReconcileFromServerStamp(message);
            newState = ComputeStateLocked();
        }

        StateChanged?.Invoke(this, newState);
    }

    public SessionTrackerState GetCurrentState()
    {
        lock (_gate)
        {
            return ComputeStateLocked();
        }
    }

    private static long ReconcileFromServerStamp(SessionMessage message)
    {
        // Don't catch-up when the server says locked — server already accounts for
        // it and RemainingSec is the literal truth at the moment of the publish.
        if (message.Locked)
        {
            return message.RemainingSec;
        }

        if (!DateTimeOffset.TryParse(
                message.UpdatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var serverStamp))
        {
            // Malformed updatedAt — best effort: skip the catch-up rather than risk
            // a wildly wrong subtraction. The ongoing monotonic tick still runs.
            return message.RemainingSec;
        }

        var deltaSec = (DateTimeOffset.UtcNow - serverStamp).TotalSeconds;
        if (deltaSec <= 0)
        {
            // Server stamp is at or in the future of our wall clock (clock skew,
            // freshly published). No catch-up needed.
            return message.RemainingSec;
        }

        return Math.Max(0, message.RemainingSec - (long)deltaSec);
    }

    private SessionTrackerState ComputeStateLocked()
    {
        if (_latest is null)
        {
            return SessionTrackerState.NoSession;
        }

        long effective;
        var locked = _latest.Locked;
        if (_latest.Locked)
        {
            effective = _latest.RemainingSec;
        }
        else
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _anchorTimestamp;
            var elapsedSec = (long)(elapsedTicks / (double)Stopwatch.Frequency);
            effective = Math.Max(0, _remainingAtAnchor - elapsedSec);
            if (effective == 0)
            {
                locked = true;
            }
        }

        return new SessionTrackerState(
            HasSession: true,
            SessionId: _latest.SessionId,
            StationId: _latest.StationId,
            DeviceType: _latest.DeviceType,
            Locked: locked,
            EffectiveRemainingSec: effective,
            LastCreditSeqNo: _latest.LastCreditSeqNo,
            ServerUpdatedAt: _latest.UpdatedAt);
    }
}
