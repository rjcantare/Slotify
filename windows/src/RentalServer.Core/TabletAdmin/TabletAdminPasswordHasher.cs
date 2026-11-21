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

public sealed record TabletAdminPasswordHash(string SaltBase64, string HashBase64);
