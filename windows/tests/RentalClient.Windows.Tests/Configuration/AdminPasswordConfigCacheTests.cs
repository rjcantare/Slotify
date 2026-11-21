using System.Security.Cryptography;
using System.Text;
using RentalClient.Windows.Configuration;
using RentalShared.Mqtt;

namespace RentalClient.Windows.Tests.Configuration;

public sealed class AdminPasswordConfigCacheTests
{
    [Fact]
    public void Verifier_uses_cached_server_hash_when_present()
    {
        var config = MakeConfig("server-pass");

        Assert.True(AdminPasswordVerifier.Verify("server-pass", config, allowFallback: false));
        Assert.False(AdminPasswordVerifier.Verify(AdminPasswordVerifier.FallbackAdminPassword, config, allowFallback: false));
    }

    [Fact]
    public void Verifier_allows_fallback_only_without_cached_config()
    {
        Assert.True(AdminPasswordVerifier.Verify(
            AdminPasswordVerifier.FallbackAdminPassword,
            config: null,
            allowFallback: true));
        Assert.False(AdminPasswordVerifier.Verify(
            AdminPasswordVerifier.FallbackAdminPassword,
            config: null,
            allowFallback: false));
    }

    [Fact]
    public void Cache_round_trips_admin_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"admin-cache-{Guid.NewGuid():N}.json");
        var cache = AdminPasswordConfigCache.FromPath(path);
        var config = MakeConfig("server-pass");

        try
        {
            cache.Save(config);
            var loaded = cache.Load();

            Assert.NotNull(loaded);
            Assert.True(AdminPasswordVerifier.Verify("server-pass", loaded, allowFallback: false));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static TabletAdminConfigMessage MakeConfig(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var combined = new byte[salt.Length + passwordBytes.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(passwordBytes, 0, combined, salt.Length, passwordBytes.Length);

        return new TabletAdminConfigMessage
        {
            Version = 1,
            UpdatedAt = "2026-07-03T00:00:00.000Z",
            Algorithm = "sha256-salt-prefix-v1",
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(SHA256.HashData(combined))
        };
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
