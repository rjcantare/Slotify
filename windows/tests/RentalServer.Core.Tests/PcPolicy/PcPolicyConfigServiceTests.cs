using RentalServer.Core.PcPolicy;

namespace RentalServer.Core.Tests.PcPolicy;

public sealed class PcPolicyConfigServiceTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private PcPolicyRepository _repository = null!;
    private FakePcPolicyConfigPublisher _publisher = null!;
    private PcPolicyConfigService _service = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-{Guid.NewGuid():N}.db");
        _repository = new PcPolicyRepository(_databasePath);
        await _repository.InitializeAsync();
        _publisher = new FakePcPolicyConfigPublisher();
        _service = new PcPolicyConfigService(_repository, _publisher);
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PublishCurrent_emits_retained_pc_policy_shape()
    {
        await _repository.EnsureSeededAsync("2026-07-03T00:00:00.000Z");

        await _service.PublishCurrentAsync();

        Assert.NotNull(_publisher.LastMessage);
        Assert.Equal(1, _publisher.LastMessage!.Version);
        Assert.False(_publisher.LastMessage.AutoCloseOnLock.Enabled);
        Assert.False(_publisher.LastMessage.IdleShutdown.Enabled);
        Assert.Equal(5, _publisher.LastMessage.IdleShutdown.IdleMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task UpdateConfig_rejects_invalid_idle_minutes(int minutes)
    {
        var config = PcPolicyConfig.Default with { IdleShutdownMinutes = minutes };

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateConfigAsync(config));
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
