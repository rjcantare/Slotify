using RentalShared.Mqtt;

namespace RentalServer.Core.Admin;

public interface IAdminCommandPublisher
{
    Task PublishCommandAsync(
        AdminCommandMessage message,
        CancellationToken cancellationToken = default);
}
