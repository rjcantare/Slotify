using System.Diagnostics;

namespace RentalServer.Core.Time;

public sealed class SystemMonotonicClock : IMonotonicClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public long Frequency => Stopwatch.Frequency;
}
