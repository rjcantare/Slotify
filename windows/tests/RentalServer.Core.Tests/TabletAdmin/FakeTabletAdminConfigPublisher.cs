using RentalServer.Core.TabletAdmin;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.TabletAdmin;

public sealed class FakeTabletAdminConfigPublisher : ITabletAdminConfigPublisher
{
    public List<TabletAdminConfigMessage> Published { get; } = new();

    public TabletAdminConfigMessage? LastMessage =>
        Published.Count == 0 ? null : Published[^1];

    public int PublishCount => Published.Count;

    public Task PublishTabletAdminAsync(
        TabletAdminConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
