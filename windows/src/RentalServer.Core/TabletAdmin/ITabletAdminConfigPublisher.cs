using RentalShared.Mqtt;

namespace RentalServer.Core.TabletAdmin;

public interface ITabletAdminConfigPublisher
{
    Task PublishTabletAdminAsync(
        TabletAdminConfigMessage message,
        CancellationToken cancellationToken = default);
}
