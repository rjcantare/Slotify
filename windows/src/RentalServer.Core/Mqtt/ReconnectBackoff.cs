namespace RentalServer.Core.Mqtt;

public sealed class ReconnectBackoff
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    private int _attempt;

    public TimeSpan NextDelay()
    {
        var index = Math.Min(_attempt, Schedule.Length - 1);
        _attempt++;
        return Schedule[index];
    }

    public void Reset()
    {
        _attempt = 0;
    }

    public int AttemptCount => _attempt;
}
