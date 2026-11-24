using System.Globalization;
using RentalServer.Core.TabletAdmin;

namespace RentalServer.Core.Tests.TabletAdmin;

public sealed class TabletAdminRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private TabletAdminRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-tabletadmin-{Guid.NewGuid():N}.db");
        _repository = new TabletAdminRepository(_databasePath);
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
    public async Task EnsureSeeded_produces_current_default_hash_config()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        Assert.Equal(1, await _repository.GetCurrentVersionAsync());
        var config = await _repository.GetCurrentConfigAsync();

        Assert.NotNull(config);
        Assert.Equal(TabletAdminPasswordHasher.Algorithm, config!.Algorithm);
        Assert.True(VerifyHash(TabletAdminRepository.DefaultPassword, config.PasswordSalt, config.PasswordHash));
    }

    [Fact]
    public async Task PublishNewPassword_replaces_current_config()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var version = await _repository.PublishNewPasswordAsync("changed-pass", FormatUtcNow());

        Assert.Equal(2, version);
        Assert.Equal(2, await _repository.GetCurrentVersionAsync());
        var config = await _repository.GetCurrentConfigAsync();
        Assert.NotNull(config);
        Assert.True(VerifyHash("changed-pass", config!.PasswordSalt, config.PasswordHash));
    }

    [Fact]
    public async Task IsDefaultPassword_is_null_before_seed_true_after_seed_false_after_change()
    {
        Assert.Null(await _repository.IsDefaultPasswordAsync());

        await _repository.EnsureSeededAsync(FormatUtcNow());
        Assert.True(await _repository.IsDefaultPasswordAsync());

        await _repository.PublishNewPasswordAsync("changed-pass", FormatUtcNow());
        Assert.False(await _repository.IsDefaultPasswordAsync());
    }

    [Fact]
    public async Task IsDefaultPassword_is_true_when_default_is_set_again()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());
        await _repository.PublishNewPasswordAsync(TabletAdminRepository.DefaultPassword, FormatUtcNow());

        Assert.True(await _repository.IsDefaultPasswordAsync());
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
