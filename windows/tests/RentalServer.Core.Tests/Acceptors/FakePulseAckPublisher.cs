using RentalServer.Core.Acceptors;

namespace RentalServer.Core.Tests.Acceptors;

public sealed class FakePulseAckPublisher : IPulseAckPublisher
{
    public List<(string AcceptorId, long SeqNo)> Published { get; } = new();

    public int PublishCount => Published.Count;

    public Task PublishPulseAckAsync(
        string acceptorId,
        long seqNo,
        CancellationToken cancellationToken = default)
    {
        Published.Add((acceptorId, seqNo));
        return Task.CompletedTask;
    }
}
