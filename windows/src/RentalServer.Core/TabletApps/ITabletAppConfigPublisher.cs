using RentalShared.Mqtt;

namespace RentalServer.Core.TabletApps;

public interface ITabletAppConfigPublisher
{
    Task PublishTabletAppsAsync(
        TabletAppConfigMessage message,
        CancellationToken cancellationToken = default);
}
