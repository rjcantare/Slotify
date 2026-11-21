using System.Diagnostics;
using RentalClient.Windows.Claims;
using RentalClient.Windows.Mqtt;

namespace RentalClient.Windows.PcPolicy;

public sealed class PcIdleShutdownCountdownTracker
{
    private readonly Func<long> _getTimestamp;
    private readonly long _frequency;
    private long? _eligibleSince;

    public PcIdleShutdownCountdownTracker()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    public PcIdleShutdownCountdownTracker(Func<long> getTimestamp, long frequency)
    {
        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        _frequency = frequency;
    }

    public int? Update(PcIdleShutdownCountdownInput input)
    {
        if (!IsEligible(input))
        {
            Reset();
            return null;
        }

        var now = _getTimestamp();
        _eligibleSince ??= now;
        var thresholdSec = checked(Math.Max(1, input.IdleMinutes) * 60);
        var elapsedSec = (long)((now - _eligibleSince.Value) / (double)_frequency);
        return (int)Math.Max(0, thresholdSec - elapsedSec);
    }

    public void Reset()
    {
        _eligibleSince = null;
    }

    private static bool IsEligible(PcIdleShutdownCountdownInput input)
    {
        if (!input.PolicyEnabled ||
            !string.Equals(input.DeviceType, "pc", StringComparison.OrdinalIgnoreCase) ||
            input.ConnectionMode != ClientConnectionMode.OnlineAuthoritative ||
            !input.HasSession ||
            !input.Locked ||
            input.RemainingSec > 0)
        {
            return false;
        }

        return input.ClaimStatus is not ClaimStatus.Pending and not ClaimStatus.Granted;
    }
}

public sealed record PcIdleShutdownCountdownInput(
    bool PolicyEnabled,
    int IdleMinutes,
    string DeviceType,
    ClientConnectionMode ConnectionMode,
    bool HasSession,
    bool Locked,
    long RemainingSec,
    ClaimStatus ClaimStatus);
