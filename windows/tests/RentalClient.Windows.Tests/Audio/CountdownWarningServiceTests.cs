using RentalClient.Windows.Audio;
using RentalClient.Windows.Configuration;
using RentalClient.Windows.Sessions;

namespace RentalClient.Windows.Tests.Audio;

public sealed class CountdownWarningServiceTests
{
    [Fact]
    public void Plays_first_warning_once_when_crossing_threshold()
    {
        var player = new FakeWarningSoundPlayer();
        var service = CreateService(player);

        Assert.Null(service.Update(Active(61)));
        AssertCue(service.Update(Active(60)), CountdownWarningCueKind.FirstWarning, 60);
        Assert.Null(service.Update(Active(60)));
        Assert.Null(service.Update(Active(59)));

        Assert.Single(player.Cues);
    }

    [Fact]
    public void Plays_final_countdown_once_per_second()
    {
        var player = new FakeWarningSoundPlayer();
        var service = CreateService(player);

        AssertCue(service.Update(Active(60)), CountdownWarningCueKind.FirstWarning, 60);
        Assert.Null(service.Update(Active(11)));
        AssertCue(service.Update(Active(10)), CountdownWarningCueKind.FinalCountdown, 10);
        Assert.Null(service.Update(Active(10)));
        AssertCue(service.Update(Active(9)), CountdownWarningCueKind.FinalCountdown, 9);
        AssertCue(service.Update(Active(1)), CountdownWarningCueKind.FinalCountdown, 1);
        Assert.Null(service.Update(Active(0)));

        Assert.Equal(new long[] { 60, 10, 9, 1 }, player.Cues.Select(c => c.RemainingSec));
    }

    [Fact]
    public void Credit_extension_above_threshold_resets_warning_state()
    {
        var player = new FakeWarningSoundPlayer();
        var service = CreateService(player);

        AssertCue(service.Update(Active(60)), CountdownWarningCueKind.FirstWarning, 60);
        AssertCue(service.Update(Active(10)), CountdownWarningCueKind.FinalCountdown, 10);

        Assert.Null(service.Update(Active(90)));
        AssertCue(service.Update(Active(60)), CountdownWarningCueKind.FirstWarning, 60);
        AssertCue(service.Update(Active(10)), CountdownWarningCueKind.FinalCountdown, 10);

        Assert.Equal(
            new[]
            {
                CountdownWarningCueKind.FirstWarning,
                CountdownWarningCueKind.FinalCountdown,
                CountdownWarningCueKind.FirstWarning,
                CountdownWarningCueKind.FinalCountdown
            },
            player.Cues.Select(c => c.Kind));
    }

    [Fact]
    public void Locked_or_missing_session_suppresses_and_resets_warnings()
    {
        var player = new FakeWarningSoundPlayer();
        var service = CreateService(player);

        AssertCue(service.Update(Active(10)), CountdownWarningCueKind.FinalCountdown, 10);
        Assert.Null(service.Update(Locked(0)));
        Assert.Null(service.Update(SessionTrackerState.NoSession));
        AssertCue(service.Update(Active(10)), CountdownWarningCueKind.FinalCountdown, 10);

        Assert.Equal(new long[] { 10, 10 }, player.Cues.Select(c => c.RemainingSec));
    }

    [Fact]
    public void New_session_resets_warning_state()
    {
        var player = new FakeWarningSoundPlayer();
        var service = CreateService(player);

        AssertCue(service.Update(Active(60, "s-1")), CountdownWarningCueKind.FirstWarning, 60);
        Assert.Null(service.Update(Active(59, "s-1")));
        AssertCue(service.Update(Active(60, "s-2")), CountdownWarningCueKind.FirstWarning, 60);

        Assert.Equal(2, player.Cues.Count);
    }

    private static CountdownWarningService CreateService(FakeWarningSoundPlayer player)
    {
        return new CountdownWarningService(
            new CountdownWarningConfig
            {
                Enabled = true,
                FirstWarningSec = 60,
                FinalCountdownStartSec = 10
            },
            player);
    }

    private static SessionTrackerState Active(long remainingSec, string sessionId = "s-1")
    {
        return new SessionTrackerState(
            HasSession: true,
            SessionId: sessionId,
            StationId: "pc-01",
            DeviceType: "pc",
            Locked: false,
            EffectiveRemainingSec: remainingSec,
            LastCreditSeqNo: 1,
            ServerUpdatedAt: "2026-05-20T00:00:00.000Z");
    }

    private static SessionTrackerState Locked(long remainingSec, string sessionId = "s-1")
    {
        return Active(remainingSec, sessionId) with { Locked = true };
    }

    private static void AssertCue(
        CountdownWarningCue? cue,
        CountdownWarningCueKind kind,
        long remainingSec)
    {
        Assert.NotNull(cue);
        Assert.Equal(kind, cue.Kind);
        Assert.Equal(remainingSec, cue.RemainingSec);
    }

    private sealed class FakeWarningSoundPlayer : ICountdownWarningSoundPlayer
    {
        public List<CountdownWarningCue> Cues { get; } = [];

        public void Play(CountdownWarningCue cue)
        {
            Cues.Add(cue);
        }
    }
}
