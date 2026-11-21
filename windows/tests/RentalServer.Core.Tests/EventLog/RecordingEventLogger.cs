using RentalServer.Core.EventLog;

namespace RentalServer.Core.Tests.EventLog;

/// <summary>
/// In-memory fake used to assert that the right rows get written by services
/// without spinning up a real SQLite file. The hash chain is mocked out
/// because tests of that mechanic live in EventLogRepositoryTests; here we
/// only care that the service emitted with the right shape.
/// </summary>
public sealed class RecordingEventLogger : IEventLogger
{
    public List<EventLogAppendRequest> Appended { get; } = new();

    public int Count => Appended.Count;

    public EventLogAppendRequest? Last =>
        Appended.Count == 0 ? null : Appended[^1];

    public Task<long> AppendAsync(
        EventLogAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        Appended.Add(request);
        return Task.FromResult((long)Appended.Count);
    }
}
