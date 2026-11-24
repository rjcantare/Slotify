using System.Security.Cryptography;
using System.Text;

namespace RentalServer.Core.TabletAdmin;

public static class TabletAdminPasswordHasher
{
    public const string Algorithm = "sha256-salt-prefix-v1";

    public static TabletAdminPasswordHash HashNewPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new TabletAdminPasswordHash(
            Convert.ToBase64String(salt),
            Convert.ToBase64String(HashPassword(password, salt)));
    }

    public static byte[] HashPassword(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var input = new byte[salt.Length + passwordBytes.Length];
        Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
        Buffer.BlockCopy(passwordBytes, 0, input, salt.Length, passwordBytes.Length);
        return SHA256.HashData(input);
    }
}

public static class TabletAdminPasswordCheck
{
    /// <summary>True when <paramref name="password"/> matches the stored salt + hash.</summary>
    public static bool Matches(string password, string saltBase64, string hashBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            return CryptographicOperations.FixedTimeEquals(
                TabletAdminPasswordHasher.HashPassword(password, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record TabletAdminPasswordHash(string SaltBase64, string HashBase64);
