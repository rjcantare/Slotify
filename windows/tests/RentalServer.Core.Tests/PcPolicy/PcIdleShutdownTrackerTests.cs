using RentalServer.Core.PcPolicy;
using RentalServer.Core.Tests.Sessions;

namespace RentalServer.Core.Tests.PcPolicy;

public sealed class PcIdleShutdownTrackerTests
{
    [Fact]
    public void Disabled_config_never_dispatches()
    {
        var clock = new FakeMonotonicClock();
        var tracker = new PcIdleShutdownTracker(clock);

        tracker.Update(PcPolicyConfig.Default, new[] { LockedPc("pc-01") });
        clock.AdvanceSeconds(10 * 60);
        var targets = tracker.Update(PcPolicyConfig.Default, new[] { LockedPc("pc-01") });

        Assert.Empty(targets);
    }

    [Fact]
    public void Dispatches_after_idle_threshold_once_per_idle_period()
    {
        var clock = new FakeMonotonicClock();
        var tracker = new PcIdleShutdownTracker(clock);
        var config = PcPolicyConfig.Default with
        {
            IdleShutdownEnabled = true,
            IdleShutdownMinutes = 5
        };

        tracker.Update(config, new[] { LockedPc("pc-01") });
        clock.AdvanceSeconds(5 * 60 - 1);
        Assert.Empty(tracker.Update(config, new[] { LockedPc("pc-01") }));

        clock.AdvanceSeconds(1);
        Assert.Equal(new[] { "pc-01" }, tracker.Update(config, new[] { LockedPc("pc-01") }));
        clock.AdvanceSeconds(60);
        Assert.Empty(tracker.Update(config, new[] { LockedPc("pc-01") }));
    }

    [Theory]
    [MemberData(nameof(SkippedStations))]
    public void Skips_non_idle_candidates(PcIdleStationSnapshot station)
    {
        var clock = new FakeMonotonicClock();
        var tracker = new PcIdleShutdownTracker(clock);
        var config = PcPolicyConfig.Default with
        {
            IdleShutdownEnabled = true,
            IdleShutdownMinutes = 5
        };

        tracker.Update(config, new[] { station });
        clock.AdvanceSeconds(10 * 60);

        Assert.Empty(tracker.Update(config, new[] { station }));
    }

    public static IEnumerable<object[]> SkippedStations()
    {
        yield return new object[] { LockedPc("pc-01") with { IsOnline = false } };
        yield return new object[] { LockedPc("phn-01") with { DeviceType = "phn" } };
        yield return new object[] { LockedPc("tab-01") with { DeviceType = "tab" } };
        yield return new object[] { LockedPc("pc-01") with { LiveRemainingSeconds = 1 } };
        yield return new object[] { LockedPc("pc-01") with { HasActiveClaim = true } };
    }

    private static PcIdleStationSnapshot LockedPc(string stationId) =>
        new(
            StationId: stationId,
            DeviceType: "pc",
            IsOnline: true,
            LiveRemainingSeconds: 0,
            HasActiveClaim: false);
}
