using System.Diagnostics;

namespace RentalClient.Windows;

/// <summary>
/// Keeps the RentalClient logon scheduled-task launch delay short so the kiosk
/// lock appears quickly after boot. The S38 installer registered the task with a
/// 20 s AtLogOn delay; the Windows OTA only swaps the EXE and never re-runs the
/// installer (see App.TryApplyPendingUpdate), so already-deployed PCs keep the
/// 20 s delay until something fixes the task itself. This self-heals it to 5 s
/// on launch — the boot after an OTA picks up the shorter delay.
///
/// Best-effort by design: a missing task, insufficient rights, or PowerShell
/// being unavailable are all swallowed so it can never delay or crash the kiosk.
/// Modifying the installer-created task needs admin rights; the iCafe8 rental
/// user is an admin. Runs on a background thread so it never blocks the UI.
/// </summary>
internal static class AutostartTuner
{
    // One read-modify-write: only rewrites when the current delay is missing or
    // > 5 s, so steady-state boots are a no-op. Uses the ScheduledTasks module —
    // the same mechanism client-install-autostart.cmd uses to register the task.
    private const string PatchScript =
        "$ErrorActionPreference='Stop';" +
        "$t = Get-ScheduledTask -TaskName 'RentalClient';" +
        "$d = $t.Triggers[0].Delay;" +
        "$needs = $true;" +
        "if ($d) { try { if ([System.Xml.XmlConvert]::ToTimeSpan($d).TotalSeconds -le 5) { $needs = $false } } catch {} };" +
        "if ($needs) { $t.Triggers[0].Delay = 'PT5S'; $t | Set-ScheduledTask | Out-Null }";

    public static Task EnsureFastLogonDelayAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        "-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
                        "-WindowStyle Hidden -Command \"" + PatchScript + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc is null) return;

                // Bounded wait: the kiosk never depends on this finishing.
                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }
            catch
            {
                // Best-effort: autostart tuning must never affect the kiosk.
            }
        });
    }
}
