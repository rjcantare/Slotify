using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using RentalServer.Core.Acceptors;
using RentalServer.Core.Claims;
using RentalServer.Core.Credits;
using RentalServer.Core.EventLog;
using RentalServer.Core.Mqtt;
using RentalServer.Core.Persistence;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;

namespace RentalServer;

public partial class HealthWindow : Window
{
    private readonly AcceptorRepository _acceptorRepo;
    private readonly StationRepository _stationRepo;
    private readonly SessionService _sessionService;
    private readonly ClaimService _claimService;
    private readonly PendingCreditsRepository _pendingCreditsRepo;
    private readonly EventLogRepository _eventLogRepo;
    private readonly RentalServerMqttService _mqtt;
    private readonly string _databasePath;
    private readonly ObservableCollection<StationHealthRow> _rows = new();

    public HealthWindow(
        AcceptorRepository acceptorRepo,
        StationRepository stationRepo,
        SessionService sessionService,
        ClaimService claimService,
        PendingCreditsRepository pendingCreditsRepo,
        EventLogRepository eventLogRepo,
        RentalServerMqttService mqtt,
        string databasePath)
    {
        InitializeComponent();
        _acceptorRepo = acceptorRepo;
        _stationRepo = stationRepo;
        _sessionService = sessionService;
        _claimService = claimService;
        _pendingCreditsRepo = pendingCreditsRepo;
        _eventLogRepo = eventLogRepo;
        _mqtt = mqtt;
        _databasePath = databasePath;
        StationHealthGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_databasePath)!;
            var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(dir, $"diagnostics-{ts}.zip");

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            // Database snapshot
            if (File.Exists(_databasePath))
            {
                var tmp = Path.GetTempFileName();
                File.Copy(_databasePath, tmp, overwrite: true);
                zip.CreateEntryFromFile(tmp, "rental.db");
                File.Delete(tmp);
            }

            // Event log dump
            var events = await _eventLogRepo.QueryAsync(new EventLogQuery { Limit = 500 });
            var eventCsv = new StringBuilder("ts,event_type,station_id,acceptor_id,amount_pesos,seconds_credited,payload\n");
            foreach (var ev in events)
            {
                eventCsv.AppendFormat(
                    "{0},{1},{2},{3},{4},{5},\"{6}\"\n",
                    ev.Ts, ev.EventType, ev.StationId ?? "", ev.AcceptorId ?? "",
                    ev.AmountPesos?.ToString() ?? "", ev.SecondsCredited?.ToString() ?? "",
                    ev.Payload.Replace("\"", "\"\""));
            }
            zip.CreateEntryFromString("event_log.csv", eventCsv.ToString());

            // Station status snapshot
            var sb = new StringBuilder();
            foreach (var row in _rows)
            {
                sb.AppendLine($"{row.StationId}\t{row.DeviceType}\t{row.OnlineText}\t{row.AppVersion}\t{row.SessionState}\t{row.TimeLeft}");
            }
            zip.CreateEntryFromString("stations.tsv", sb.ToString());

            MessageBox.Show($"Diagnostics exported to:\n{zipPath}", "Export Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        // Broker health
        BrokerDot.Fill = _mqtt.IsConnected
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        BrokerStatusText.Text = _mqtt.IsConnected ? "Connected" : "Disconnected";

        // Acceptor health
        var acceptors = await _acceptorRepo.GetAllAsync();
        if (acceptors.Count == 0)
        {
            AcceptorHealthText.Text = "No acceptor registered";
            AcceptorHealthText.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var a in acceptors)
            {
                var online = a.IsOnline ? "ONLINE" : "OFFLINE";
                var rssi = a.LastRssi is not null ? $" RSSI={a.LastRssi}" : "";
                sb.AppendLine($"{a.AcceptorId}: {online} v{a.FirmwareVersion ?? "?"}{rssi}");
            }
            AcceptorHealthText.Text = sb.ToString().TrimEnd();
            AcceptorHealthText.Foreground = acceptors.Any(a => a.IsOnline)
                ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }

        // Database health
        if (File.Exists(_databasePath))
        {
            var info = new FileInfo(_databasePath);
            var sizeMb = info.Length / (1024.0 * 1024.0);
            var hasWal = File.Exists(_databasePath + "-wal");
            DatabaseHealthText.Text = $"SQLite {sizeMb:F1} MB, WAL={(hasWal ? "yes" : "no")}";
            DatabaseHealthText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        }
        else
        {
            DatabaseHealthText.Text = "Database not found";
            DatabaseHealthText.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }

        // Pending credits
        var pendingCount = await _pendingCreditsRepo.CountAsync();
        PendingCreditsText.Text = pendingCount == 0
            ? "None"
            : $"{pendingCount} awaiting ACK";
        PendingCreditsText.Foreground = pendingCount == 0
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));

        // Station health rows
        _rows.Clear();
        var stations = await _stationRepo.GetAllAsync();
        foreach (var s in stations)
        {
            var remaining = _sessionService.GetLiveRemainingSeconds(s.StationId);
            _rows.Add(new StationHealthRow
            {
                StationId = s.StationId,
                DeviceType = s.DeviceType,
                OnlineText = s.IsOnline ? "Yes" : "No",
                AppVersion = s.AppVersion ?? "?",
                SessionState = remaining switch
                {
                    null => "No session",
                    <= 0 => "LOCKED",
                    _ => "Active"
                },
                TimeLeft = remaining is null ? "\u2014" : FormatDuration(remaining.Value)
            });
        }
    }

    private static string FormatDuration(int totalSeconds)
    {
        var h = totalSeconds / 3600;
        var m = (totalSeconds % 3600) / 60;
        var s = totalSeconds % 60;
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    private sealed class StationHealthRow
    {
        public string StationId { get; init; } = string.Empty;
        public string DeviceType { get; init; } = string.Empty;
        public string OnlineText { get; init; } = string.Empty;
        public string AppVersion { get; init; } = string.Empty;
        public string SessionState { get; init; } = string.Empty;
        public string TimeLeft { get; init; } = string.Empty;
    }
}

internal static class ZipArchiveExtensions
{
    public static void CreateEntryFromString(this ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
