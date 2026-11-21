using RentalServer.Core.EventLog;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace RentalServer;

/// <summary>
/// S33: daily coin-insertion history for the operator. Sister to
/// TransactionsWindow, but stripped down to plain-language columns —
/// no JSON payload, no hash chain, no internal event_type enum. The
/// operator sees: when, where, how much, how long added, was it a coin
/// or admin Add Time.
///
/// Default view is today (local time). Older days reachable via the
/// date picker. The DB stores ISO 8601 UTC strings, so the date filter
/// converts the chosen local date to a [00:00 local, 24:00 local) UTC
/// half-open window before querying.
/// </summary>
public partial class CoinHistoryWindow : Window
{
    private readonly EventLogRepository _repository;
    private readonly ObservableCollection<CoinHistoryRow> _rows = new();

    public CoinHistoryWindow(EventLogRepository repository)
    {
        _repository = repository;
        InitializeComponent();

        HistoryGrid.ItemsSource = _rows;
        DatePicker.SelectedDate = DateTime.Today;

        // Load synchronously on first open. SelectedDateChanged is what fires
        // ReloadAsync — set it AFTER InitializeComponent so the assignment
        // above triggers the load.
    }

    private async void OnDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DatePicker.SelectedDate is null)
        {
            return;
        }

        try
        {
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            // Async void in a WPF event handler escapes to the dispatcher
            // unhandled-exception pipeline if it throws. Swallow + surface
            // in the totals strip so the window stays open.
            TotalsText.Text = $"Error loading history: {ex.Message}";
        }
    }

    private async void OnTodayClicked(object sender, RoutedEventArgs e)
    {
        DatePicker.SelectedDate = DateTime.Today;
        await ReloadAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var date = DatePicker.SelectedDate ?? DateTime.Today;

        // Build a [start-of-day, start-of-next-day) UTC range. The DB stores
        // ISO 8601 UTC; "today's coin history" means coins that fell on the
        // operator's calendar day, which is local time, so we anchor to local.
        var localTz = TimeZoneInfo.Local;
        var startLocal = new DateTimeOffset(date.Date, localTz.GetUtcOffset(date.Date));
        var endLocal = startLocal.AddDays(1);

        var fromTs = FormatUtc(startLocal);
        var toTs = FormatUtc(endLocal);

        // Three queries because EventLogQuery filters by a single event_type;
        // merging is cheaper than adding an OR clause to the repository.
        // We pull coin drops, admin Add Time, AND admin Remove Time so the
        // operator's daily view doesn't hide manual time adjustments.
        IReadOnlyList<EventLogRecord> coinRows;
        IReadOnlyList<EventLogRecord> claimEndedRows;
        IReadOnlyList<EventLogRecord> adminAddRows;
        IReadOnlyList<EventLogRecord> adminRemoveRows;
        try
        {
            coinRows = await _repository.QueryAsync(new EventLogQuery(
                FromTs: fromTs,
                ToTs: toTs,
                EventType: EventTypes.CoinCredit,
                StationId: null));
            claimEndedRows = await _repository.QueryAsync(new EventLogQuery(
                FromTs: fromTs,
                ToTs: toTs,
                EventType: EventTypes.ClaimEnded,
                StationId: null));
            adminAddRows = await _repository.QueryAsync(new EventLogQuery(
                FromTs: fromTs,
                ToTs: toTs,
                EventType: EventTypes.AdminCredit,
                StationId: null));
            adminRemoveRows = await _repository.QueryAsync(new EventLogQuery(
                FromTs: fromTs,
                ToTs: toTs,
                EventType: EventTypes.AdminRemoveTime,
                StationId: null));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to load coin history: {ex.Message}",
                "Coin History",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var starterDisplaySecondsByEventId = BuildStarterDisplaySecondsLookup(
            coinRows,
            claimEndedRows);

        var combined = coinRows
            .Concat(adminAddRows)
            .Concat(adminRemoveRows)
            .OrderByDescending(r => r.Id) // latest first
            .ToList();

        _rows.Clear();
        foreach (var r in combined)
        {
            _rows.Add(MapToRow(r, starterDisplaySecondsByEventId));
        }

        // Totals — denormalized columns make this a SUM over the in-memory
        // list. Remove Time events contribute their seconds_credited as a
        // NEGATIVE in the net-added total so the bottom strip reflects the
        // operator's actual position for the day.
        var totalPesos = combined
            .Select(EffectiveAmountPesos)
            .Where(pesos => pesos.HasValue)
            .Sum(pesos => pesos!.Value);
        var totalAddedSec = combined
            .Where(r => r.EventType != EventTypes.AdminRemoveTime)
            .Select(r => DisplaySecondsCredited(r, starterDisplaySecondsByEventId))
            .Where(seconds => seconds.HasValue)
            .Sum(seconds => seconds!.Value);
        var totalRemovedSec = combined
            .Where(r => r.SecondsCredited.HasValue && r.EventType == EventTypes.AdminRemoveTime)
            .Sum(r => r.SecondsCredited!.Value);
        var netSec = totalAddedSec - totalRemovedSec;

        TotalsText.Text =
            combined.Count == 0
                ? $"No coin or admin activity on {date:MMM d, yyyy}."
                : totalRemovedSec == 0
                    ? $"{combined.Count} event(s) on {date:MMM d, yyyy}.  " +
                      $"Total ₱{totalPesos}.  Total time added: {FormatDuration(totalAddedSec)}."
                    : $"{combined.Count} event(s) on {date:MMM d, yyyy}.  " +
                      $"Total ₱{totalPesos}.  " +
                      $"Added: {FormatDuration(totalAddedSec)}, " +
                      $"removed: {FormatDuration(totalRemovedSec)}, " +
                      $"net: {FormatDuration(netSec)}.";
    }

    private static IReadOnlyDictionary<long, int> BuildStarterDisplaySecondsLookup(
        IReadOnlyList<EventLogRecord> coinRows,
        IReadOnlyList<EventLogRecord> claimEndedRows)
    {
        var result = new Dictionary<long, int>();
        var starterSummaries = claimEndedRows
            .Where(IsStarterClaimSummary)
            .Select(r => new
            {
                ClaimId = GetClaimId(r),
                TotalPesos = EffectiveClaimTotalPesos(r),
                TotalSeconds = r.SecondsCredited ?? 0
            })
            .Where(x => x.ClaimId is not null && x.TotalPesos is > 0 && x.TotalSeconds > 0)
            .ToDictionary(x => x.ClaimId!, StringComparer.Ordinal);

        var groupedCoins = coinRows
            .Where(r => string.Equals(GetRateMode(r), "insert_coin", StringComparison.Ordinal))
            .Select(r => new { Row = r, ClaimId = GetClaimId(r), Amount = EffectiveAmountPesos(r) })
            .Where(x => x.ClaimId is not null && x.Amount is > 0)
            .GroupBy(x => x.ClaimId!, StringComparer.Ordinal);

        foreach (var group in groupedCoins)
        {
            if (!starterSummaries.TryGetValue(group.Key, out var summary))
            {
                continue;
            }

            var coins = group.OrderBy(x => x.Row.Id).ToList();
            var visibleTotalPesos = coins.Sum(x => x.Amount!.Value);
            var splitByPesos = visibleTotalPesos == summary.TotalPesos
                ? summary.TotalPesos.Value
                : visibleTotalPesos;
            if (splitByPesos <= 0)
            {
                continue;
            }

            var remainingSeconds = summary.TotalSeconds;
            for (var i = 0; i < coins.Count; i++)
            {
                var amount = coins[i].Amount!.Value;
                var seconds = i == coins.Count - 1
                    ? remainingSeconds
                    : (int)Math.Floor((double)summary.TotalSeconds * amount / splitByPesos);
                seconds = Math.Max(0, seconds);
                result[coins[i].Row.Id] = seconds;
                remainingSeconds -= seconds;
            }
        }

        return result;
    }

    private static CoinHistoryRow MapToRow(
        EventLogRecord r,
        IReadOnlyDictionary<long, int> starterDisplaySecondsByEventId)
    {
        // event_log stores ISO 8601 UTC; display as local HH:mm so the
        // operator reads "what time did this happen on the wall clock."
        var local = DateTimeOffset.Parse(
                r.Ts,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            .ToLocalTime();

        var amountPesos = EffectiveAmountPesos(r);
        var amountText = r.AmountPesos is > 0
            ? $"₱{r.AmountPesos}"
            : "—"; // admin Add Time / Remove Time has no peso amount
        if (amountPesos is > 0)
        {
            amountText = $"\u20b1{amountPesos}";
        }

        var isPendingStarterCoin =
            r.EventType == EventTypes.CoinCredit &&
            string.Equals(GetRateMode(r), "insert_coin", StringComparison.Ordinal) &&
            DisplaySecondsCredited(r, starterDisplaySecondsByEventId) is null or <= 0;

        var displaySeconds = DisplaySecondsCredited(r, starterDisplaySecondsByEventId);
        var addedText = isPendingStarterCoin
            ? "Below minimum"
            : displaySeconds is > 0
            ? (r.EventType == EventTypes.AdminRemoveTime
                ? $"-{FormatDuration(displaySeconds.Value)}"
                : FormatDuration(displaySeconds.Value))
            : "—";

        var addedForeground = isPendingStarterCoin ? "#C62828" : "#222222";

        var sourceLabel = r.EventType switch
        {
            EventTypes.CoinCredit when string.Equals(GetRateMode(r), "insert_coin", StringComparison.Ordinal) => "Starter coin",
            EventTypes.CoinCredit when string.Equals(GetRateMode(r), "add_time", StringComparison.Ordinal) => "Add-time coin",
            EventTypes.CoinCredit => "Coin drop",
            EventTypes.AdminCredit => "Add Time (admin)",
            EventTypes.AdminRemoveTime => "Remove Time (admin)",
            _ => r.EventType
        };

        return new CoinHistoryRow(
            TimeOfDay: local.ToString("HH:mm", CultureInfo.InvariantCulture),
            Station: r.StationId ?? "(unknown)",
            AmountText: amountText,
            AddedText: addedText,
            AddedForeground: addedForeground,
            SourceLabel: sourceLabel);
    }

    private static int? DisplaySecondsCredited(
        EventLogRecord r,
        IReadOnlyDictionary<long, int> starterDisplaySecondsByEventId)
    {
        return starterDisplaySecondsByEventId.TryGetValue(r.Id, out var splitSeconds)
            ? splitSeconds
            : r.SecondsCredited;
    }

    private static int? EffectiveAmountPesos(EventLogRecord r)
    {
        var amount = r.AmountPesos;
        if (r.EventType != EventTypes.CoinCredit || string.IsNullOrWhiteSpace(r.Payload))
        {
            return amount;
        }

        try
        {
            using var document = JsonDocument.Parse(r.Payload);
            var root = document.RootElement;

            if (root.TryGetProperty("pulseCount", out var pulseCountElement) &&
                pulseCountElement.TryGetInt32(out var pulseCount) &&
                pulseCount > 0)
            {
                amount = amount.HasValue ? Math.Max(amount.Value, pulseCount) : pulseCount;
            }

            if (root.TryGetProperty("pesoAmount", out var pesoAmountElement) &&
                pesoAmountElement.TryGetInt32(out var payloadPesoAmount) &&
                payloadPesoAmount > 0)
            {
                amount = amount.HasValue ? Math.Max(amount.Value, payloadPesoAmount) : payloadPesoAmount;
            }

        }
        catch (JsonException)
        {
            // Older/tampered payloads still fall back to the denormalized column.
        }

        return amount;
    }

    private static int? EffectiveClaimTotalPesos(EventLogRecord r)
    {
        var amount = r.AmountPesos;
        if (string.IsNullOrWhiteSpace(r.Payload))
        {
            return amount;
        }

        try
        {
            using var document = JsonDocument.Parse(r.Payload);
            var root = document.RootElement;

            if (root.TryGetProperty("accumulatedPesos", out var accumulatedPesosElement) &&
                accumulatedPesosElement.TryGetInt32(out var accumulatedPesos) &&
                accumulatedPesos > 0)
            {
                amount = amount.HasValue ? Math.Max(amount.Value, accumulatedPesos) : accumulatedPesos;
            }
        }
        catch (JsonException)
        {
        }

        return amount;
    }

    private static bool IsStarterClaimSummary(EventLogRecord r)
    {
        return r.EventType == EventTypes.ClaimEnded &&
               r.SecondsCredited is > 0 &&
               string.Equals(GetRateMode(r), "insert_coin", StringComparison.Ordinal);
    }

    private static string? GetClaimId(EventLogRecord r)
    {
        return GetPayloadString(r, "claimId");
    }

    private static string? GetRateMode(EventLogRecord r)
    {
        return GetPayloadString(r, "rateMode");
    }

    private static string? GetPayloadString(EventLogRecord r, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(r.Payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(r.Payload);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
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

/// <summary>
/// View-model row for the HistoryGrid. Pre-formatted display strings so the
/// XAML binds to plain text without converters or templates.
/// </summary>
public sealed record CoinHistoryRow(
    string TimeOfDay,
    string Station,
    string AmountText,
    string AddedText,
    string AddedForeground,
    string SourceLabel);
