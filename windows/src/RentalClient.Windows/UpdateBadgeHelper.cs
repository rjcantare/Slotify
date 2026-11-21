using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using RentalClient.Windows.Claims;
using RentalShared.Updates;
using WpfMessageBox = System.Windows.MessageBox;

namespace RentalClient.Windows;

/// <summary>
/// Shared update-badge state and download logic, extracted from MainWindow
/// so LockOverlayWindow, ActiveSessionOverlayWindow, and AdminModeWindow
/// can all show and handle the same update badge without duplicating code.
/// </summary>
public class UpdateBadgeHelper
{
    private UpdateManifest? _manifest;
    private bool _downloadInProgress;

    public void SetManifest(UpdateManifest? manifest)
    {
        _manifest = manifest;
    }

    /// <summary>
    /// Returns badge visibility, label text, and tooltip for the current state.
    /// Returns (false, "", "") when the badge should be hidden.
    /// </summary>
    public (bool Visible, string Text, string Tooltip) GetBadgeState(ClaimStatus? claimStatus)
    {
        var pending = _manifest?.Client;
        if (pending is null || string.IsNullOrWhiteSpace(pending.Version) || string.IsNullOrWhiteSpace(pending.Url))
            return (false, string.Empty, string.Empty);

        if (!TryCompareUpdateVersions(pending.Version, out var publishedIsNewer) || !publishedIsNewer)
            return (false, string.Empty, string.Empty);

        if (_downloadInProgress)
            return (false, string.Empty, string.Empty);

        if (claimStatus is ClaimStatus.Pending or ClaimStatus.Granted)
            return (false, string.Empty, string.Empty);

        var sizeMb = pending.SizeBytes / 1024 / 1024;
        return (
            true,
            $"Update v{pending.Version}",
            $"Update v{pending.Version} available ({sizeMb} MB). Click to download and apply.");
    }

    /// <summary>
    /// Downloads the client update, verifies SHA-256, and writes the staging file.
    /// Shows MessageBox dialogs on the provided parent window.
    /// Returns true when the .exe.new file was written successfully.
    /// </summary>
    public async Task<bool> DownloadAndStageAsync(Window parent, CancellationToken ct)
    {
        var pending = _manifest?.Client;
        if (pending is null || _downloadInProgress) return false;

        var current = GetRunningExeVersion()?.ToString() ?? "unknown";
        var sizeMb = pending.SizeBytes / 1024 / 1024;
        var prompt = $"Update v{pending.Version} available (current: {current}).\n" +
                     $"Download size: {sizeMb} MB\n\n" +
                     "Download now? The new version will apply on the next client restart.";
        if (WpfMessageBox.Show(parent, prompt, "Update Available",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        _downloadInProgress = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync(pending.Url, ct);

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, pending.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                WpfMessageBox.Show(parent,
                    $"SHA-256 mismatch — refusing to write the update.\nExpected: {pending.Sha256[..16]}...\nGot:      {actualHash[..16]}...",
                    "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var exePath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("ProcessPath unavailable; cannot stage update.");
            var bakPath = System.IO.Path.ChangeExtension(exePath, ".exe.bak");
            var newExePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                System.IO.Path.GetFileName(System.IO.Path.ChangeExtension(exePath, ".exe.new")));
            await System.IO.File.WriteAllBytesAsync(newExePath, bytes, ct);

            LaunchSwapAndExit(exePath, newExePath, bakPath);

            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            WpfMessageBox.Show(parent, $"Update download failed: {ex.Message}",
                "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _downloadInProgress = false;
        }
    }

    private static void LaunchSwapAndExit(string exePath, string newExePath, string bakPath)
    {
        var ps1Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rental-client-update-swap.ps1");

        System.IO.File.WriteAllText(ps1Path,
            $"$exePath = '{exePath.Replace("'", "''")}'\r\n" +
            $"$newExePath = '{newExePath.Replace("'", "''")}'\r\n" +
            $"$bakPath = '{bakPath.Replace("'", "''")}'\r\n" +
            "Start-Sleep 2\r\n" +
            "try {\r\n" +
            "  if (Test-Path $bakPath) { Remove-Item $bakPath -Force }\r\n" +
            "  Move-Item $exePath $bakPath -Force -ErrorAction SilentlyContinue\r\n" +
            "  Move-Item $newExePath $exePath -Force\r\n" +
            "  Start-Process $exePath\r\n" +
            "} catch {\r\n" +
            "  Write-Error $_\r\n" +
            "  Start-Sleep 10\r\n" +
            "}\r\n" +
            "Remove-Item $PSCommandPath -Force\r\n");

        // Elevate so the swap can write to Program Files. This shows a UAC
        // prompt — acceptable because the operator is present when clicking
        // the update badge (this is an admin action, not a customer workflow).
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{ps1Path}\"",
            UseShellExecute = true,
            Verb = "RunAs",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => System.Windows.Application.Current.Shutdown(0));
    }

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
}
