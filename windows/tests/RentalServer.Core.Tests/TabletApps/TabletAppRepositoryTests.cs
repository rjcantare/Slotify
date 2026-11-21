using System.Globalization;
using RentalServer.Core.TabletApps;

namespace RentalServer.Core.Tests.TabletApps;

public sealed class TabletAppRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private TabletAppRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-tabletapps-{Guid.NewGuid():N}.db");
        _repository = new TabletAppRepository(_databasePath);
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureSeeded_produces_current_default_config()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        Assert.Equal(1, await _repository.GetCurrentVersionAsync());
        var config = await _repository.GetCurrentConfigAsync();

        Assert.NotNull(config);
        Assert.Contains(config!.Apps, app => app.PackageName == "com.google.android.youtube");
        Assert.All(config.Apps, app => Assert.True(app.DeepFreeze));
        Assert.Equal(
            TabletAppCategories.VideoSocial,
            config.Apps.Single(app => app.PackageName == "com.google.android.youtube").Category);
    }

    [Fact]
    public async Task PublishNewVersion_replaces_current_config()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var newConfig = new TabletAppConfig(
        [
            new("YouTube", "com.google.android.youtube", Enabled: true, DeepFreeze: true, SortOrder: 0),
            new("Zoom", "us.zoom.videomeetings", Enabled: true, DeepFreeze: false, SortOrder: 1)
        ]);

        var version = await _repository.PublishNewVersionAsync(newConfig, FormatUtcNow());

        Assert.Equal(2, version);
        Assert.Equal(2, await _repository.GetCurrentVersionAsync());
        var current = await _repository.GetCurrentConfigAsync();
        Assert.NotNull(current);
        Assert.Equal(2, current!.Apps.Count);
        Assert.False(current.Apps.Single(a => a.PackageName == "us.zoom.videomeetings").DeepFreeze);
        Assert.Equal(TabletAppCategories.Other, current.Apps.Single(a => a.PackageName == "us.zoom.videomeetings").Category);
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
