using RentalShared.Mqtt;

namespace RentalServer.Core.Sessions;

public interface ICreditPublisher
{
    Task PublishCreditAsync(
        string stationId,
        CreditMessage message,
        CancellationToken cancellationToken = default);
}
