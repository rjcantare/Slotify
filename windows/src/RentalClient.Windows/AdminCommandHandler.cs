using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using RentalClient.Windows.Mqtt;
using RentalShared.Mqtt;

namespace RentalClient.Windows;

// S49: routes server-issued admin commands into the right OS-level action.
//
// Flow:
//   server publishes rental/station/{id}/command
//     -> StationMqttClient raises AdminCommandReceived
//     -> handler validates + publishes ack (acknowledged/rejected)
//     -> handler executes the requested action
//
// Restart / reboot / shutdown race their own ack against the OS process
// teardown. We publish the ack first and then introduce a small delay
// before exec so the broker has the QoS-1 PUBACK in hand. The server
// already treats a missing ack on those commands as the expected outcome,
// but landing the ack when we can keeps the audit trail tidier.
public sealed class AdminCommandHandler
{
    private const int RestartGracePeriodMs = 500;
    private const int OsCommandGracePeriodSec = 5;
    private const string ShutdownTitle = "RentalSystem admin command";

    private readonly StationMqttClient _mqtt;
    private readonly string _stationId;
    private readonly HashSet<string> _seenRequestIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AdminCommandHandler(StationMqttClient mqtt, string stationId)
    {
        _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        _stationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
    }

    public async Task HandleAsync(AdminCommandMessage command, CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            return;
        }

        // Server addresses commands by topic; the stationId field is a sanity
        // check so a misrouted message can't trigger us by accident.
        if (!string.Equals(command.StationId, _stationId, StringComparison.Ordinal))
        {
            await TryPublishAckAsync(
                command.RequestId,
                result: "rejected",
                detail: $"stationId mismatch: expected {_stationId}, got {command.StationId}",
                cancellationToken);
            return;
        }

        // QoS 1 + clean_session=false means the broker may redeliver a command
        // we already started processing. The exec paths kill the process so
        // re-entry across a restart cycle is harmless; this guard just stops
        // a duplicate ack from churning the audit log in the same run.
        lock (_gate)
        {
            if (!_seenRequestIds.Add(command.RequestId))
            {
                return;
            }
        }

        switch (command.Command)
        {
            case "restart_client":
                await TryPublishAckAsync(
                    command.RequestId,
                    result: "acknowledged",
                    detail: "client will restart",
                    cancellationToken);
                ExecuteRestartClient();
                break;

            case "reboot_pc":
                await TryPublishAckAsync(
                    command.RequestId,
                    result: "acknowledged",
                    detail: $"reboot in {OsCommandGracePeriodSec}s",
                    cancellationToken);
                ExecuteShutdownCommand("/r");
                break;

            case "shutdown_pc":
                await TryPublishAckAsync(
                    command.RequestId,
                    result: "acknowledged",
                    detail: $"shutdown in {OsCommandGracePeriodSec}s",
                    cancellationToken);
                ExecuteShutdownCommand("/s");
                break;

            default:
                await TryPublishAckAsync(
                    command.RequestId,
                    result: "rejected",
                    detail: $"unknown command: {command.Command}",
                    cancellationToken);
                break;
        }
    }

    private async Task TryPublishAckAsync(
        string requestId,
        string result,
        string detail,
        CancellationToken cancellationToken)
    {
        try
        {
            await _mqtt.PublishCommandAckAsync(requestId, result, detail, cancellationToken);
        }
        catch
        {
            // Broker disconnected mid-handler. The QoS-1 retry queue would
            // normally cover this, but a shutdown is about to kill us anyway.
            // The server treats absent acks on restart/reboot/shutdown as the
            // expected outcome; swallow so OS teardown still proceeds.
        }
    }

    private static void ExecuteRestartClient()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Assembly.GetExecutingAssembly().Location;
        var workingDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        // Marshal to the UI thread for Shutdown(); the new process picks up
        // where this one leaves off using the same appsettings.json.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    // --restart makes the new instance WAIT for this (old) one
                    // to release the single-instance mutex on exit, instead of
                    // fail-fast "already running". Without it the replacement
                    // races the mutex, loses, and the station goes dark.
                    Arguments = "--restart",
                    WorkingDirectory = workingDir,
                    UseShellExecute = false
                });
            }
            catch
            {
                // If spawning fails the operator will still see the station go
                // offline via LWT; manual recovery is the documented fallback.
            }

            // Tiny pause so the broker can flush the ack PUBACK before we die.
            // The new instance is already blocked on the mutex by the time we
            // get here, so it acquires the moment OnExit releases below.
            Thread.Sleep(RestartGracePeriodMs);
            System.Windows.Application.Current?.Shutdown();
        }));
    }

    private static void ExecuteShutdownCommand(string mode)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"{mode} /t {OsCommandGracePeriodSec} /c \"{ShutdownTitle}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            // Shutdown.exe is part of Windows; failure usually means the user
            // lacks SeShutdownPrivilege. The operator will see no station
            // status flip and can investigate manually.
        }
    }
}
