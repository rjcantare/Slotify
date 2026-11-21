using RentalClient.Windows.Claims;
using RentalClient.Windows.Mqtt;
using RentalClient.Windows.PcPolicy;

namespace RentalClient.Windows.Tests.PcPolicy;

public sealed class PcIdleShutdownCountdownTrackerTests
{
    private long _ticks;

    [Fact]
    public void Counts_down_while_locked_pc_is_online_and_policy_enabled()
    {
        var tracker = NewTracker();

        Assert.Equal(300, tracker.Update(Eligible()));

        AdvanceSeconds(61);

        Assert.Equal(239, tracker.Update(Eligible()));
    }

    [Theory]
    [MemberData(nameof(IneligibleInputs))]
    public void Hides_when_not_eligible(PcIdleShutdownCountdownInput input)
    {
        var tracker = NewTracker();

        Assert.Null(tracker.Update(input));
    }

    [Fact]
    public void Resets_after_ineligible_state()
    {
        var tracker = NewTracker();

        Assert.Equal(300, tracker.Update(Eligible()));
        AdvanceSeconds(100);
        Assert.Equal(200, tracker.Update(Eligible()));

        Assert.Null(tracker.Update(Eligible() with { RemainingSec = 60, Locked = false }));

        Assert.Equal(300, tracker.Update(Eligible()));
    }

    [Fact]
    public void Resets_when_policy_changes()
    {
        var tracker = NewTracker();

        Assert.Equal(300, tracker.Update(Eligible()));
        AdvanceSeconds(30);
        tracker.Reset();

        Assert.Equal(300, tracker.Update(Eligible()));
    }

    public static IEnumerable<object[]> IneligibleInputs()
    {
        yield return new object[] { Eligible() with { PolicyEnabled = false } };
        yield return new object[] { Eligible() with { DeviceType = "tab" } };
        yield return new object[] { Eligible() with { ConnectionMode = ClientConnectionMode.OfflineAutonomous } };
        yield return new object[] { Eligible() with { HasSession = false } };
        yield return new object[] { Eligible() with { Locked = false, RemainingSec = 60 } };
        yield return new object[] { Eligible() with { ClaimStatus = ClaimStatus.Pending } };
        yield return new object[] { Eligible() with { ClaimStatus = ClaimStatus.Granted } };
    }

    private PcIdleShutdownCountdownTracker NewTracker() =>
        new(() => _ticks, frequency: 1000);

    private void AdvanceSeconds(int seconds)
    {
        _ticks += seconds * 1000L;
    }

    private static PcIdleShutdownCountdownInput Eligible() =>
        new(
            PolicyEnabled: true,
            IdleMinutes: 5,
            DeviceType: "pc",
            ConnectionMode: ClientConnectionMode.OnlineAuthoritative,
            HasSession: true,
            Locked: true,
            RemainingSec: 0,
            ClaimStatus: ClaimStatus.Idle);
}
