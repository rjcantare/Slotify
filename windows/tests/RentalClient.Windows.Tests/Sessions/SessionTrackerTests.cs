using System.Globalization;
using RentalClient.Windows.Sessions;
using RentalShared.Mqtt;

namespace RentalClient.Windows.Tests.Sessions;

public sealed class SessionTrackerTests
{
    [Fact]
    public void Apply_skips_re_anchor_when_redelivered_retained_is_byte_identical()
    {
        // S20.9.1 regression: broker resubscribes (e.g. duplicate client IDs
        // kicking each other) re-deliver the same retained session every few
        // seconds. Without this guard, every redelivery re-anchored to
        // RemainingAtAnchor=300 and the countdown looked frozen at 5:00.
        var tracker = new SessionTracker();
        var raised = 0;
        tracker.StateChanged += (_, _) => raised++;

        var message = new SessionMessage
        {
            SessionId = "s-1",
            StationId = "pc-01",
            DeviceType = "pc",
            Locked = false,
            RemainingSec = 300,
            LastCreditSeqNo = 5,
            UpdatedAt = "2026-05-21T12:00:00.000Z"
        };

        tracker.Apply(message);

        // Take a snapshot of the elapsed-since-anchor state so we can prove the
        // tracker did NOT restart its monotonic anchor on the second apply.
        Thread.Sleep(50);
        var stateBeforeRedelivery = tracker.GetCurrentState();

        // Re-deliver an equal-by-value (record equality) message. Same instance
        // or a fresh-allocated record — both should be rejected by the equality
        // guard.
        tracker.Apply(message with { });

        var stateAfterRedelivery = tracker.GetCurrentState();

        Assert.Equal(stateBeforeRedelivery.EffectiveRemainingSec, stateAfterRedelivery.EffectiveRemainingSec);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Apply_still_accepts_equal_seqNo_when_lock_flag_flipped()
    {
        // The S14a contract preserved: equal seqNo with a different locked flag
        // (e.g. tick-induced lock with no new credit) still applies.
        var tracker = new SessionTracker();
        var unlocked = new SessionMessage
        {
            SessionId = "s-1",
            StationId = "pc-01",
            DeviceType = "pc",
            Locked = false,
            RemainingSec = 60,
            LastCreditSeqNo = 7,
            UpdatedAt = "2026-05-21T12:00:00.000Z"
        };
        tracker.Apply(unlocked);

        var locked = unlocked with { Locked = true, RemainingSec = 0, UpdatedAt = "2026-05-21T12:01:00.000Z" };
        tracker.Apply(locked);

        Assert.True(tracker.GetCurrentState().Locked);
    }

    [Fact]
    public void Apply_treats_stale_unlocked_zero_effective_time_as_locked()
    {
        var tracker = new SessionTracker();

        tracker.Apply(new SessionMessage
        {
            SessionId = "s-1",
            StationId = "pc-01",
            DeviceType = "pc",
            Locked = false,
            RemainingSec = 60,
            LastCreditSeqNo = 7,
            UpdatedAt = DateTimeOffset.UtcNow
                .AddMinutes(-5)
                .UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
        });

        var state = tracker.GetCurrentState();

        Assert.True(state.Locked);
        Assert.Equal(0, state.EffectiveRemainingSec);
    }

    [Fact]
    public void Apply_rejects_strictly_older_seqNo()
    {
        var tracker = new SessionTracker();
        tracker.Apply(MakeMessage(seqNo: 10, remainingSec: 100));
        tracker.Apply(MakeMessage(seqNo: 9, remainingSec: 999));

        // The older message must not displace the newer one.
        Assert.Equal(10, tracker.GetCurrentState().LastCreditSeqNo);
    }

    private static SessionMessage MakeMessage(long seqNo, int remainingSec)
    {
        return new SessionMessage
        {
            SessionId = "s-1",
            StationId = "pc-01",
            DeviceType = "pc",
            Locked = false,
            RemainingSec = remainingSec,
            LastCreditSeqNo = seqNo,
            UpdatedAt = "2026-05-21T12:00:00.000Z"
        };
    }
}
