using RentalShared.Mqtt;

namespace RentalServer.Core.Claims;

public interface IClaimResultPublisher
{
    Task PublishClaimResultAsync(
        string stationId,
        ClaimResultMessage message,
        CancellationToken cancellationToken = default);
}
