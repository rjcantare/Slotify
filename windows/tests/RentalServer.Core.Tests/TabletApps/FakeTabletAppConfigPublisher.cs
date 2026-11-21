using RentalServer.Core.TabletApps;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.TabletApps;

public sealed class FakeTabletAppConfigPublisher : ITabletAppConfigPublisher
{
    public List<TabletAppConfigMessage> Published { get; } = new();

    public TabletAppConfigMessage? LastMessage =>
        Published.Count == 0 ? null : Published[^1];

    public int PublishCount => Published.Count;

    public Task PublishTabletAppsAsync(
        TabletAppConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
