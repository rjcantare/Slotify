using System.Diagnostics;

namespace RentalClient.Windows.Locking;

public sealed class WindowsAppCloser
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(3);

    public async Task CloseUserAppsAsync(CancellationToken cancellationToken = default)
    {
        var current = Process.GetCurrentProcess();
        var targets = Process.GetProcesses()
            .Where(process => ShouldTarget(process, current))
            .ToArray();

        foreach (var process in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryClose(process);
        }

        try
        {
            await Task.Delay(GracePeriod, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var process in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryKillIfStillRunning(process);
        }
    }

    private static bool ShouldTarget(Process process, Process current)
    {
        try
        {
            var candidate = new ProcessCloseCandidate(
                process.Id,
                process.ProcessName,
                process.SessionId,
                process.MainWindowHandle != IntPtr.Zero);

            return WindowsAppClosePolicy.ShouldClose(
                candidate,
                current.Id,
                current.SessionId);
        }
        catch
        {
            return false;
        }
    }

    private static void TryClose(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
            }
        }
        catch
        {
        }
    }

    private static void TryKillIfStillRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
