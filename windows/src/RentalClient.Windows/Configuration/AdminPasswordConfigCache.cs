using System.IO;
using System.Text.Json;
using RentalShared.Mqtt;

namespace RentalClient.Windows.Configuration;

public sealed class AdminPasswordConfigCache
{
    private readonly string _path;

    public AdminPasswordConfigCache(string stationId)
        : this(GetDefaultPath(stationId), directPath: true)
    {
    }

    private AdminPasswordConfigCache(string path, bool directPath)
    {
        _path = path;
    }

    public static AdminPasswordConfigCache FromPath(string path) =>
        new(path, directPath: true);

    public TabletAdminConfigMessage? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            var message = JsonSerializer.Deserialize<TabletAdminConfigMessage>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return IsUsable(message) ? message : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(TabletAdminConfigMessage message)
    {
        if (!IsUsable(message))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_path, json);
    }

    private static bool IsUsable(TabletAdminConfigMessage? message)
    {
        return message is not null &&
               !string.IsNullOrWhiteSpace(message.Algorithm) &&
               !string.IsNullOrWhiteSpace(message.PasswordSalt) &&
               !string.IsNullOrWhiteSpace(message.PasswordHash);
    }

    private static string GetDefaultPath(string stationId)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(
            root,
            "RentalSystem",
            "Client",
            SanitizePathSegment(stationId),
            "admin-password-cache.json");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "station" : sanitized;
    }
}
