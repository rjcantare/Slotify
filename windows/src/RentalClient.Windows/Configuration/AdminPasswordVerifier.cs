using System.Security.Cryptography;
using System.Text;
using RentalShared.Mqtt;

namespace RentalClient.Windows.Configuration;

public static class AdminPasswordVerifier
{
    public const string FallbackAdminPassword = "admin@123!";
    private const string SupportedAlgorithm = "sha256-salt-prefix-v1";

    public static bool Verify(
        string entered,
        TabletAdminConfigMessage? config,
        bool allowFallback)
    {
        if (config is not null &&
            !string.IsNullOrWhiteSpace(config.PasswordSalt) &&
            !string.IsNullOrWhiteSpace(config.PasswordHash) &&
            string.Equals(config.Algorithm, SupportedAlgorithm, StringComparison.Ordinal))
        {
            try
            {
                var salt = Convert.FromBase64String(config.PasswordSalt);
                var expectedHash = Convert.FromBase64String(config.PasswordHash);
                var passwordBytes = Encoding.UTF8.GetBytes(entered);
                var combined = new byte[salt.Length + passwordBytes.Length];
                Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
                Buffer.BlockCopy(passwordBytes, 0, combined, salt.Length, passwordBytes.Length);
                var actualHash = SHA256.HashData(combined);
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            catch
            {
                return false;
            }
        }

        return allowFallback && entered == FallbackAdminPassword;
    }
}
