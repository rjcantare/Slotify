namespace RentalServer.Core.Acceptors;

public interface IPulseAckPublisher
{
    Task PublishPulseAckAsync(
        string acceptorId,
        long seqNo,
        CancellationToken cancellationToken = default);
}
