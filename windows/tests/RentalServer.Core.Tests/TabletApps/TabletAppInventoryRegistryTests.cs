using System.Text.Json;
using RentalServer.Core.TabletApps;
using RentalShared.Mqtt;

namespace RentalServer.Core.Tests.TabletApps;

public sealed class TabletAppInventoryRegistryTests
{
    [Fact]
    public async Task HandleInventoryMessage_stores_tablet_launchable_apps()
    {
        var registry = new TabletAppInventoryRegistry();
        var payload = JsonSerializer.Serialize(new TabletAppInventoryMessage
        {
            StationId = "tab-01",
            DeviceType = "tab",
            PublishedAt = "2026-05-26T00:00:00Z",
            Apps =
            [
                new TabletInstalledAppMessage
                {
                    DisplayName = "YouTube",
                    PackageName = "com.google.android.youtube",
                    System = true
                }
            ]
        });

        await registry.HandleInventoryMessageAsync("rental/station/tab-01/apps", payload);

        var inventory = Assert.Single(registry.GetAll());
        Assert.Equal("tab-01", inventory.StationId);
        Assert.Equal("com.google.android.youtube", Assert.Single(inventory.Apps).PackageName);
    }

    [Fact]
    public async Task HandleInventoryMessage_ignores_non_tablet_inventory()
    {
        var registry = new TabletAppInventoryRegistry();
        var payload = JsonSerializer.Serialize(new TabletAppInventoryMessage
        {
            StationId = "pc-01",
            DeviceType = "pc",
            PublishedAt = "2026-05-26T00:00:00Z",
            Apps =
            [
                new TabletInstalledAppMessage { DisplayName = "Chrome", PackageName = "com.android.chrome" }
            ]
        });

        await registry.HandleInventoryMessageAsync("rental/station/pc-01/apps", payload);

        Assert.Empty(registry.GetAll());
    }
}
