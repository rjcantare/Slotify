using RentalServer.Core.Abuse;

namespace RentalServer.Core.Tests.Abuse;

public sealed class AbusePreventionRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private AbusePreventionRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");
        _repository = new AbusePreventionRepository(_databasePath);
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
    public async Task EnsureSeeded_creates_default_current_config()
    {
        await _repository.EnsureSeededAsync("2026-07-03T00:00:00.000Z");

        var config = await _repository.GetCurrentConfigAsync();
        var version = await _repository.GetCurrentVersionAsync();

        Assert.Equal(1, version);
        Assert.Equal(AbusePreventionService.DefaultConfig, config);
    }

    [Fact]
    public async Task PublishNewVersion_persists_current_config()
    {
        await _repository.EnsureSeededAsync("2026-07-03T00:00:00.000Z");

        var version = await _repository.PublishNewVersionAsync(
            new AbusePreventionService.Config(
                Enabled: true,
                MaxEmptyClaims: 4,
                BanMinutes: 6),
            "2026-07-03T01:00:00.000Z");

        var config = await _repository.GetCurrentConfigAsync();
        var publishedAt = await _repository.GetCurrentPublishedAtAsync();

        Assert.Equal(2, version);
        Assert.Equal(
            new AbusePreventionService.Config(true, 4, 6),
            config);
        Assert.Equal("2026-07-03T01:00:00.000Z", publishedAt);
    }

    [Fact]
    public async Task PublishNewVersion_rejects_invalid_values()
    {
        await _repository.EnsureSeededAsync("2026-07-03T00:00:00.000Z");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.PublishNewVersionAsync(
                new AbusePreventionService.Config(
                    Enabled: true,
                    MaxEmptyClaims: 0,
                    BanMinutes: 1),
                "2026-07-03T01:00:00.000Z"));
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
