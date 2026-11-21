using RentalServer.Core.Sessions;

namespace RentalServer.Core.Tests.Sessions;

public sealed class FakeSessionPublisher : ISessionPublisher
{
    public List<SessionState> Published { get; } = new();

    public int PublishCount => Published.Count;

    public SessionState? LastState => Published.Count == 0 ? null : Published[^1];

    public Task PublishSessionAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        Published.Add(state);
        return Task.CompletedTask;
    }
}
