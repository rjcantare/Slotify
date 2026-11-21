using RentalServer.Core.Time;

namespace RentalServer.Core.PcPolicy;

public sealed class PcIdleShutdownTracker
{
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<string, long> _idleSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dispatched = new(StringComparer.OrdinalIgnoreCase);

    public PcIdleShutdownTracker(IMonotonicClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IReadOnlyList<string> Update(
        PcPolicyConfig config,
        IReadOnlyCollection<PcIdleStationSnapshot> stations)
    {
        var now = _clock.GetTimestamp();
        var idleThresholdTicks = checked((long)config.IdleShutdownMinutes * 60 * _clock.Frequency);
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var station in stations)
        {
            seen.Add(station.StationId);
            if (!config.IdleShutdownEnabled || !IsIdleCandidate(station))
            {
                _idleSince.Remove(station.StationId);
                _dispatched.Remove(station.StationId);
                continue;
            }

            if (!_idleSince.TryGetValue(station.StationId, out var idleSince))
            {
                _idleSince[station.StationId] = now;
                continue;
            }

            if (now - idleSince >= idleThresholdTicks &&
                _dispatched.Add(station.StationId))
            {
                targets.Add(station.StationId);
            }
        }

        foreach (var stationId in _idleSince.Keys.ToArray())
        {
            if (!seen.Contains(stationId))
            {
                _idleSince.Remove(stationId);
                _dispatched.Remove(stationId);
            }
        }

        return targets;
    }

    private static bool IsIdleCandidate(PcIdleStationSnapshot station)
    {
        return station.IsOnline &&
               string.Equals(station.DeviceType, "pc", StringComparison.OrdinalIgnoreCase) &&
               station.LiveRemainingSeconds <= 0 &&
               !station.HasActiveClaim;
    }
}

public sealed record PcIdleStationSnapshot(
    string StationId,
    string DeviceType,
    bool IsOnline,
    long LiveRemainingSeconds,
    bool HasActiveClaim);
