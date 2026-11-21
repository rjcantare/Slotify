using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using RentalShared.Updates;

namespace RentalClient.Windows.Updates;

internal static class UpdateHelper
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static Version? GetRunningExeVersion()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath))
            return null;
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
        var raw = info.FileVersion;
        if (string.IsNullOrWhiteSpace(raw) || !Version.TryParse(raw, out var v))
            return null;
        if (v.Major == 0 && v.Minor == 0 && v.Build <= 0 && v.Revision <= 0)
            return null;
        return v;
    }

    public static bool TryCompareUpdateVersions(string published, out bool publishedIsNewer)
    {
        publishedIsNewer = false;
        if (string.IsNullOrWhiteSpace(published) || !Version.TryParse(published, out var pv))
            return false;
        var current = GetRunningExeVersion();
        if (current is null)
        {
            publishedIsNewer = true;
            return true;
        }
        var normalizedPv = new Version(pv.Major, pv.Minor, Math.Max(pv.Build, 0), Math.Max(pv.Revision, 0));
        var normalizedCv = new Version(current.Major, current.Minor, Math.Max(current.Build, 0), Math.Max(current.Revision, 0));
        publishedIsNewer = normalizedPv > normalizedCv;
        return true;
    }

    public static bool IsNewerAvailable(UpdateComponent? component)
    {
        if (component is null || string.IsNullOrWhiteSpace(component.Version) || string.IsNullOrWhiteSpace(component.Url))
            return false;
        if (!TryCompareUpdateVersions(component.Version, out var newer))
            return false;
        return newer;
    }

    public static string BadgeText(UpdateComponent? component)
    {
        if (component is null) return "Update available";
        return $"Update v{component.Version}";
    }

    public static string BadgeToolTip(UpdateComponent? component)
    {
        if (component is null) return "";
        return $"Update v{component.Version} available ({component.SizeBytes / 1024 / 1024} MB). Click to download, then restart to apply.";
    }

    public static async Task<string?> DownloadUpdateAsync(Window owner, UpdateComponent component, CancellationToken ct)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(component.Url, ct);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, component.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"SHA-256 mismatch.\nExpected: {component.Sha256[..16]}...\nGot:      {actualHash[..16]}...";
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return "ProcessPath unavailable; cannot stage update.";

            var newExePath = System.IO.Path.ChangeExtension(exePath, ".exe.new");
            await System.IO.File.WriteAllBytesAsync(newExePath, bytes, ct);
            return null;
        }
        catch (OperationCanceledException) { return "Download cancelled."; }
        catch (Exception ex) { return $"Update download failed: {ex.Message}"; }
    }
}
