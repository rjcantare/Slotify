using System.Diagnostics;
using System.IO;
using System.Windows;

namespace RentalServer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Global\RentalServer";
    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// S50a: runs once at app startup to swap a pending .new EXE into place.
    /// Returns true if the process should exit (the swap script is running).
    /// </summary>
    private static bool TryApplyPendingUpdate()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        var newExePath = Path.ChangeExtension(exePath, ".exe.new");
        if (!File.Exists(newExePath)) return false;

        LaunchSwapBatch(exePath, newExePath);

        return true;
    }

    /// <summary>
    /// Writes a batch script to %TEMP% that waits 2 s, swaps the old EXE with
    /// the new one, and re-launches. Caller must then call Shutdown(0) to
    /// release the file lock so the script's move commands can succeed.
    /// </summary>
    public static void LaunchSwapBatch(string exePath, string newExePath)
    {
        var bakPath = Path.ChangeExtension(exePath, ".exe.bak");
        var scriptPath = Path.Combine(Path.GetTempPath(), "rental-server-update-swap.cmd");

        File.WriteAllText(scriptPath,
            $"@echo off\r\n" +
            $"echo Swapping RentalServer update...\r\n" +
            $"timeout /t 2 /nobreak >nul\r\n" +
            $"move /Y \"{exePath}\" \"{bakPath}\"\r\n" +
            $"move /Y \"{newExePath}\" \"{exePath}\"\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            $"del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // S50a: check for pending update BEFORE the mutex guard, so the
        // swap script can launch the new EXE cleanly.
        if (TryApplyPendingUpdate())
        {
            Shutdown(exitCode: 0);
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: false, name: MutexName);
        bool acquired;
        try
        {
            // Zero timeout: a duplicate server should fail fast instead of
            // joining MQTT/SQLite and fighting the real authority process.
            acquired = _singleInstanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            MessageBox.Show(
                "RentalServer is already running on this machine.\n\n" +
                "Only one server instance may own the MQTT broker connection and SQLite database.",
                "RentalServer already running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(exitCode: 1);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException)
            {
                // Startup can exit before ownership is established.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}

