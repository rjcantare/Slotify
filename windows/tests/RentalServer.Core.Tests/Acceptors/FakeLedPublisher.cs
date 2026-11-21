using RentalServer.Core.Acceptors;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.Acceptors;

public sealed class FakeLedPublisher : ILedPublisher
{
    public List<(string AcceptorId, LedCommandMessage Message)> Published { get; } = new();

    public int PublishCount => Published.Count;

    public LedCommandMessage? LastMessage => Published.Count == 0 ? null : Published[^1].Message;

    public Task PublishLedAsync(
        string acceptorId,
        LedCommandMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add((acceptorId, message));
        return Task.CompletedTask;
    }
}
