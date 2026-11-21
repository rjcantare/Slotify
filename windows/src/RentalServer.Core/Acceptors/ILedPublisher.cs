using RentalShared.Mqtt;

namespace RentalServer.Core.Acceptors;

public interface ILedPublisher
{
    Task PublishLedAsync(
        string acceptorId,
        LedCommandMessage message,
        CancellationToken cancellationToken = default);
}
