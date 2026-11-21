using System.Globalization;
using RentalServer.Core.TabletApps;

namespace RentalServer.Core.Tests.TabletApps;

public sealed class TabletAppConfigServiceTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private TabletAppRepository _repository = null!;
    private FakeTabletAppConfigPublisher _publisher = null!;
    private TabletAppConfigService _service = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-tabletappsvc-{Guid.NewGuid():N}.db");
        _repository = new TabletAppRepository(_databasePath);
        await _repository.InitializeAsync();

        _publisher = new FakeTabletAppConfigPublisher();
        _service = new TabletAppConfigService(_repository, _publisher);
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PublishCurrent_emits_retained_tablet_app_config()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        await _service.PublishCurrentAsync();

        Assert.Equal(1, _publisher.PublishCount);
        Assert.Equal(1, _publisher.LastMessage!.Version);
        Assert.Contains(_publisher.LastMessage.Apps, app => app.PackageName == "com.roblox.client");
    }

    [Fact]
    public async Task UpdateConfig_persists_and_publishes_wire_shape()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());
        var config = new TabletAppConfig(
        [
            new("YouTube", "com.google.android.youtube", Enabled: true, DeepFreeze: true, SortOrder: 0),
            new("TikTok", "com.zhiliaoapp.musically", Enabled: false, DeepFreeze: true, SortOrder: 1)
        ]);

        var version = await _service.UpdateConfigAsync(config);

        Assert.Equal(2, version);
        Assert.Equal(2, _publisher.LastMessage!.Version);
        Assert.False(_publisher.LastMessage.Apps.Single(a => a.PackageName == "com.zhiliaoapp.musically").Enabled);
        Assert.Equal(TabletAppCategories.Other, _publisher.LastMessage.Apps.Single(a => a.PackageName == "com.zhiliaoapp.musically").Category);
    }

    [Fact]
    public async Task UpdateConfig_rejects_duplicate_packages()
    {
        var config = new TabletAppConfig(
        [
            new("A", "com.example.app", Enabled: true, DeepFreeze: true, SortOrder: 0),
            new("B", "com.example.app", Enabled: true, DeepFreeze: true, SortOrder: 1)
        ]);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateConfigAsync(config));
        Assert.Equal(0, _publisher.PublishCount);
    }

    [Fact]
    public async Task UpdateConfig_rejects_unknown_category()
    {
        var config = new TabletAppConfig(
        [
            new("A", "com.example.app", Enabled: true, DeepFreeze: true, SortOrder: 0, Category: "bad")
        ]);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateConfigAsync(config));
        Assert.Equal(0, _publisher.PublishCount);
    }

    private static string FormatUtcNow()
    {
        return DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
