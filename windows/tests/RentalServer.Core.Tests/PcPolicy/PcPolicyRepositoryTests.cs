using RentalServer.Core.PcPolicy;

namespace RentalServer.Core.Tests.PcPolicy;

public sealed class PcPolicyRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private PcPolicyRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");
        _repository = new PcPolicyRepository(_databasePath);
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
    public async Task EnsureSeeded_defaults_to_disabled_with_five_minute_idle_value()
    {
        await _repository.EnsureSeededAsync("2026-07-03T00:00:00.000Z");

        var config = await _repository.GetCurrentConfigAsync();

        Assert.NotNull(config);
        Assert.False(config!.AutoCloseOnLockEnabled);
        Assert.False(config.IdleShutdownEnabled);
        Assert.Equal(5, config.IdleShutdownMinutes);
    }

    [Fact]
    public async Task PublishNewVersion_replaces_current_config()
    {
        await _repository.EnsureSeededAsync("2026-07-03T00:00:00.000Z");

        var version = await _repository.PublishNewVersionAsync(
            new PcPolicyConfig(
                AutoCloseOnLockEnabled: true,
                IdleShutdownEnabled: true,
                IdleShutdownMinutes: 7),
            "2026-07-03T01:00:00.000Z");

        var config = await _repository.GetCurrentConfigAsync();

        Assert.Equal(2, version);
        Assert.NotNull(config);
        Assert.True(config!.AutoCloseOnLockEnabled);
        Assert.True(config.IdleShutdownEnabled);
        Assert.Equal(7, config.IdleShutdownMinutes);
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
