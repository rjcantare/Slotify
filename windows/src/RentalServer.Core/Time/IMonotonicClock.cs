namespace RentalServer.Core.Time;

// Monotonic clock abstraction for countdown math.
// NEVER use DateTime.UtcNow for elapsed-time calculations: wall clocks can jump backwards
// on NTP correction and silently break countdowns. GetTimestamp/Frequency mirror
// Stopwatch.GetTimestamp/Stopwatch.Frequency so SessionService can swap a fake in tests.
public interface IMonotonicClock
{
    long GetTimestamp();

    long Frequency { get; }
}
