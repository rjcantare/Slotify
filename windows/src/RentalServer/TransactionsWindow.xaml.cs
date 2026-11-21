using Microsoft.Win32;
using RentalServer.Core.EventLog;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace RentalServer;

public partial class TransactionsWindow : Window
{
    private static readonly string[] EventTypeFilters =
    {
        "(any)",
        EventTypes.CoinCredit,
        EventTypes.AdminCredit,
        EventTypes.ClaimGranted,
        EventTypes.ClaimEnded,
        EventTypes.ClaimCancelled,
        EventTypes.ClaimExpired,
        EventTypes.SessionLocked,
        EventTypes.RatePublished,
        EventTypes.PcPolicyPublished
    };

    private readonly EventLogRepository _repository;
    private readonly ObservableCollection<EventLogRecord> _rows = new();

    public TransactionsWindow(EventLogRepository repository)
    {
        _repository = repository;
        InitializeComponent();

        EventGrid.ItemsSource = _rows;

        foreach (var item in EventTypeFilters)
        {
            EventTypeBox.Items.Add(item);
        }
        EventTypeBox.SelectedIndex = 0;

        // Default to "today (local)" — pick the day with a calendar instead
        // of typing ISO. The DatePicker carries the date; the TimeTextBox
        // beside it carries HH:mm local for narrower windows. Empty time
        // defaults to 00:00 (From) / 23:59:59.999 (To) so a date-only
        // filter behaves intuitively.
        var today = DateTime.Today;
        FromDatePicker.SelectedDate = today;
        FromTimeTextBox.Text = "00:00";
        ToDatePicker.SelectedDate = today;
        ToTimeTextBox.Text = "23:59";
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (!TryBuildIsoBound(FromDatePicker.SelectedDate, FromTimeTextBox.Text, isUpperBound: false, out var fromIso, out var fromError))
        {
            MessageBox.Show(this, fromError!, "Transactions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryBuildIsoBound(ToDatePicker.SelectedDate, ToTimeTextBox.Text, isUpperBound: true, out var toIso, out var toError))
        {
            MessageBox.Show(this, toError!, "Transactions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var query = new EventLogQuery(
            FromTs: fromIso,
            ToTs: toIso,
            EventType: GetSelectedEventType(),
            StationId: NullIfBlank(StationTextBox.Text));

        IReadOnlyList<EventLogRecord> rows;
        try
        {
            rows = await _repository.QueryAsync(query);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to query event log: {ex.Message}",
                "Transactions",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _rows.Clear();
        foreach (var row in rows)
        {
            _rows.Add(row);
        }

        // Denormalized totals come from the SecondsCredited and AmountPesos
        // columns — no JSON parsing required.
        var totalPesos = rows
            .Where(r => r.AmountPesos.HasValue)
            .Sum(r => r.AmountPesos!.Value);
        var totalSeconds = rows
            .Where(r => r.SecondsCredited.HasValue)
            .Sum(r => r.SecondsCredited!.Value);

        TotalsText.Text =
            $"{rows.Count} row(s).  Total ₱{totalPesos}.  Total time credited: {FormatDuration(totalSeconds)}.";
    }

    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show(
                this,
                "Nothing to export. Refresh first.",
                "Export CSV",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"transactions-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = "csv"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await ExportToFileAsync(dialog.FileName, _rows.ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to write CSV: {ex.Message}",
                "Export CSV",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(
            this,
            $"Exported {_rows.Count} row(s) to {dialog.FileName}.",
            "Export CSV",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OnVerifyChainClicked(object sender, RoutedEventArgs e)
    {
        VerifyChainButton.IsEnabled = false;
        ChainStatusText.Text = "Verifying...";

        long? firstBad;
        try
        {
            firstBad = await _repository.VerifyChainAsync();
        }
        catch (Exception ex)
        {
            ChainStatusText.Text = $"Verify failed: {ex.Message}";
            VerifyChainButton.IsEnabled = true;
            return;
        }

        ChainStatusText.Text = firstBad is null
            ? "Chain intact. ✓"
            : $"Chain broken at id {firstBad}. ✗";
        VerifyChainButton.IsEnabled = true;
    }

    /// <summary>
    /// Combines a DatePicker selection + a HH:mm text box into a UTC ISO
    /// 8601 string. Returns null for the query bound when the date is
    /// not selected (open-ended). Returns false + an error string when
    /// the time text fails to parse. Upper-bound (To) with blank time
    /// defaults to end-of-day so a single-day filter is inclusive.
    /// </summary>
    private static bool TryBuildIsoBound(
        DateTime? date,
        string? timeText,
        bool isUpperBound,
        out string? iso,
        out string? error)
    {
        iso = null;
        error = null;

        if (date is null)
        {
            return true; // open-ended
        }

        var trimmedTime = timeText?.Trim() ?? string.Empty;
        TimeSpan timeOfDay;
        if (trimmedTime.Length == 0)
        {
            timeOfDay = isUpperBound
                ? new TimeSpan(23, 59, 59).Add(TimeSpan.FromMilliseconds(999))
                : TimeSpan.Zero;
        }
        else if (TimeSpan.TryParseExact(trimmedTime, new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss" },
                     CultureInfo.InvariantCulture, out var parsed))
        {
            timeOfDay = parsed;
        }
        else
        {
            error = $"Could not read \"{trimmedTime}\" as a time. Use HH:mm (e.g. 14:30).";
            return false;
        }

        var localDateTime = date.Value.Date.Add(timeOfDay);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        var dto = new DateTimeOffset(localDateTime, localOffset);
        iso = FormatUtc(dto);
        return true;
    }

    private string? GetSelectedEventType()
    {
        if (EventTypeBox.SelectedItem is not string selected || string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        return string.Equals(selected, "(any)", StringComparison.Ordinal) ? null : selected;
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static Task ExportToFileAsync(string path, IReadOnlyList<EventLogRecord> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,ts,event_type,station_id,acceptor_id,amount_pesos,seconds_credited,payload,prev_hash,row_hash");
        foreach (var row in rows)
        {
            sb.Append(row.Id).Append(',');
            sb.Append(EscapeCsv(row.Ts)).Append(',');
            sb.Append(EscapeCsv(row.EventType)).Append(',');
            sb.Append(EscapeCsv(row.StationId)).Append(',');
            sb.Append(EscapeCsv(row.AcceptorId)).Append(',');
            sb.Append(row.AmountPesos?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(row.SecondsCredited?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(EscapeCsv(row.Payload)).Append(',');
            sb.Append(EscapeCsv(row.PrevHash)).Append(',');
            sb.Append(EscapeCsv(row.RowHash)).AppendLine();
        }

        // UTF-8 with BOM so Excel opens with the right encoding by default.
        return File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // RFC 4180: quote if value contains comma, quote, CR, or LF.
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0s";
        }

        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        if (hours > 0)
        {
            return $"{hours}h {minutes}m {seconds}s";
        }

        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }
}
