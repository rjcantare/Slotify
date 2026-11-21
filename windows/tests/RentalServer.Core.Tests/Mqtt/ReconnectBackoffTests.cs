using RentalServer.Core.Mqtt;

namespace RentalServer.Core.Tests.Mqtt;

public sealed class ReconnectBackoffTests
{
    [Fact]
    public void NextDelay_FollowsDoublingSchedule()
    {
        var backoff = new ReconnectBackoff();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(16), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
    }

    [Fact]
    public void NextDelay_CapsAtSixtySecondsAfterScheduleEnd()
    {
        var backoff = new ReconnectBackoff();

        for (var i = 0; i < 7; i++)
        {
            backoff.NextDelay();
        }

        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
    }

    [Fact]
    public void Reset_ReturnsSequenceToOneSecond()
    {
        var backoff = new ReconnectBackoff();

        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();

        backoff.Reset();

        Assert.Equal(0, backoff.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
    }

    [Fact]
    public void AttemptCount_TracksCallsAcrossSchedule()
    {
        var backoff = new ReconnectBackoff();

        Assert.Equal(0, backoff.AttemptCount);

        backoff.NextDelay();
        Assert.Equal(1, backoff.AttemptCount);

        backoff.NextDelay();
        backoff.NextDelay();
        Assert.Equal(3, backoff.AttemptCount);
    }
}
