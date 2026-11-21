using System.Diagnostics;
using System.IO;
using System.Windows;
using RentalClient.Windows.Configuration;

namespace RentalClient.Windows;

public partial class App : System.Windows.Application
{
    private const string MutexNamePrefix = @"Global\RentalClientWindows-";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (TryApplyPendingUpdate())
        {
            Shutdown(0);
            return;
        }

        var config = TryLoadOrProvisionConfig();
        if (config is null)
        {
            Shutdown(exitCode: 0);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.StationId))
        {
            base.OnStartup(e);
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: false, name: MutexNamePrefix + config.StationId);

        // S49: a server-issued "restart_client" command launches the replacement
        // instance with --restart while this (old) instance is still shutting
        // down and still holds the single-instance mutex. Fail-fast (WaitOne
        // TimeSpan.Zero) would make the replacement see "already running" and
        // exit, killing the station. So when --restart is present we WAIT for
        // the old instance to release the mutex on its OnExit. Accidental
        // double-launches (no flag) keep the original fail-fast behavior so a
        // human who opens the EXE twice still gets the warning immediately.
        var isRestart = e.Args.Any(a =>
            string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase));
        var acquireTimeout = isRestart ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;

        bool acquired = false;
        try
        {
            acquired = _singleInstanceMutex.WaitOne(acquireTimeout, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            System.Windows.MessageBox.Show(
                $"Another RentalClient.Windows instance is already running for station '{config.StationId}'.\n\n" +
                "Two instances would fight over the MQTT client ID and cause connection cycling. " +
                "Close the other window first.",
                "Rental Client — already running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(exitCode: 1);
            return;
        }

        // Self-heal the autostart task's logon delay (was 20 s -> 5 s) on PCs
        // deployed before this build, since OTA only swaps the EXE and never
        // re-runs the installer. Fire-and-forget, best-effort, off-thread.
        _ = AutostartTuner.EnsureFastLogonDelayAsync();

        base.OnStartup(e);
    }

    /// <summary>
    /// Attempts to load the client config. If the config file is missing or
    /// invalid, shows the provisioning wizard for first-run setup. Returns
    /// null when the user closed the wizard without saving (app should exit).
    /// </summary>
    private static ClientConfig? TryLoadOrProvisionConfig()
    {
        try
        {
            return ClientConfig.LoadFromDisk();
        }
        catch (FileNotFoundException)
        {
            return RunProvisioningWizard();
        }
        catch (InvalidDataException)
        {
            return RunProvisioningWizard();
        }
        catch (Exception)
        {
            // Unknown failure — show wizard as a recovery path
            return RunProvisioningWizard();
        }
    }

    private static ClientConfig? RunProvisioningWizard()
    {
        var wizard = new ProvisioningWindow();
        wizard.ShowDialog();
        return wizard.Result;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException)
            {
                // Not the owner — can happen if base.OnStartup was bypassed.
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static bool TryApplyPendingUpdate()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        var newExePath = Path.ChangeExtension(exePath, ".exe.new");
        if (!File.Exists(newExePath)) return false;

        var bakPath = Path.ChangeExtension(exePath, ".exe.bak");
        var scriptPath = Path.Combine(Path.GetTempPath(), "rental-client-update-swap.cmd");

        File.WriteAllText(scriptPath,
            $"@echo off\r\n" +
            $"echo Swapping RentalClient update...\r\n" +
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

        return true;
    }
}
