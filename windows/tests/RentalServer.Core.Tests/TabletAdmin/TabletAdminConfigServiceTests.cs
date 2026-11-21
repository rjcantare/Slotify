using System.Globalization;
using RentalServer.Core.TabletAdmin;

namespace RentalServer.Core.Tests.TabletAdmin;

public sealed class TabletAdminConfigServiceTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private TabletAdminRepository _repository = null!;
    private FakeTabletAdminConfigPublisher _publisher = null!;
    private TabletAdminConfigService _service = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-tabletadminsvc-{Guid.NewGuid():N}.db");
        _repository = new TabletAdminRepository(_databasePath);
        await _repository.InitializeAsync();

        _publisher = new FakeTabletAdminConfigPublisher();
        _service = new TabletAdminConfigService(_repository, _publisher);
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PublishCurrent_emits_retained_hash_config_without_password()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        await _service.PublishCurrentAsync();

        Assert.Equal(1, _publisher.PublishCount);
        Assert.Equal(1, _publisher.LastMessage!.Version);
        Assert.Equal(TabletAdminPasswordHasher.Algorithm, _publisher.LastMessage.Algorithm);
        Assert.NotEmpty(_publisher.LastMessage.PasswordSalt);
        Assert.NotEmpty(_publisher.LastMessage.PasswordHash);
        Assert.DoesNotContain(TabletAdminRepository.DefaultPassword, _publisher.LastMessage.PasswordHash);
    }

    [Fact]
    public async Task UpdatePassword_persists_and_publishes_next_version()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var version = await _service.UpdatePasswordAsync("new-admin-pass");

        Assert.Equal(2, version);
        Assert.Equal(2, _publisher.LastMessage!.Version);
        Assert.True(VerifyHash("new-admin-pass", _publisher.LastMessage.PasswordSalt, _publisher.LastMessage.PasswordHash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData("short")]
    public async Task UpdatePassword_rejects_invalid_password(string password)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdatePasswordAsync(password));
        Assert.Equal(0, _publisher.PublishCount);
    }

    private static bool VerifyHash(string password, string saltBase64, string hashBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var expected = Convert.FromBase64String(hashBase64);
        var actual = TabletAdminPasswordHasher.HashPassword(password, salt);
        return actual.SequenceEqual(expected);
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
