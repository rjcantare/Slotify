using RentalServer.Core.Time;

namespace RentalServer.Core.Tests.Sessions;

// Frequency=1 means one tick equals one second, which keeps the test arithmetic readable.
// AdvanceSeconds is the only mutator — there is no AdvanceMilliseconds because S7 logic
// rounds elapsed time down to whole seconds anyway.
public sealed class FakeMonotonicClock : IMonotonicClock
{
    private long _ticks;

    public long GetTimestamp() => _ticks;

    public long Frequency => 1;

    public void AdvanceSeconds(long seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Monotonic clocks cannot go backwards.");
        }

        _ticks += seconds;
    }
}
