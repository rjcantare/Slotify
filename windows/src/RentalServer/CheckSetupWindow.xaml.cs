using System.Windows;
using System.Windows.Media;
using RentalServer.Core.SetupCheck;

namespace RentalServer;

/// <summary>
/// "Check setup": runs every first-install check (broker, firewall, network,
/// discovery, units, coin box, admin password) and shows a pass/warn/fail list
/// with a one-line fix for each problem. "Copy report" copies a short summary
/// with no passwords or PC names for asking help in the Facebook group.
/// </summary>
public partial class CheckSetupWindow : Window
{
    private readonly Func<Task<SetupSnapshot>> _gatherAsync;
    private SetupSnapshot? _snapshot;
    private IReadOnlyList<SetupCheckResult> _results = Array.Empty<SetupCheckResult>();
    private bool _running;

    public CheckSetupWindow(Func<Task<SetupSnapshot>> gatherAsync)
    {
        InitializeComponent();
        _gatherAsync = gatherAsync;
        Loaded += async (_, _) => await RunChecksAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RunChecksAsync();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnCopyReportClicked(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SetupChecker.BuildReport(_snapshot, _results));
            CopiedText.Text = "Copied. Paste it in the Slotify Facebook group.";
        }
        catch (Exception ex)
        {
            CopiedText.Text = $"Couldn't copy: {ex.Message}";
        }
    }

    private async Task RunChecksAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        RefreshButton.IsEnabled = false;
        CopyReportButton.IsEnabled = false;
        CopiedText.Text = string.Empty;
        SummaryText.Text = "Checking...";
        try
        {
            _snapshot = await _gatherAsync();
            _results = SetupChecker.Evaluate(_snapshot);
            ShowResults();
            CopyReportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Check setup failed: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _running = false;
        }
    }

    private void ShowResults()
    {
        var fails = _results.Count(r => r.Status == CheckStatus.Fail);
        var warns = _results.Count(r => r.Status == CheckStatus.Warn);

        if (fails > 0)
        {
            SummaryText.Text = $"{fails} problem(s) to fix and {warns} warning(s). Start with the red FIX items from the top.";
            SummaryBorder.Background = Brush(0xFE, 0xF2, 0xF2);
        }
        else if (warns > 0)
        {
            SummaryText.Text = $"No blocking problems. {warns} warning(s) worth a look.";
            SummaryBorder.Background = Brush(0xFF, 0xFB, 0xEB);
        }
        else
        {
            SummaryText.Text = "All checks passed. Kung may PC pa ring hindi maka-connect, tingnan ang hint sa lock screen nito.";
            SummaryBorder.Background = Brush(0xF0, 0xFD, 0xF4);
        }

        var rows = new List<Row>();
        string? lastArea = null;
        foreach (var r in _results)
        {
            rows.Add(new Row(r, showArea: r.Area != lastArea));
            lastArea = r.Area;
        }

        ResultsList.ItemsSource = rows;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private sealed class Row
    {
        public Row(SetupCheckResult result, bool showArea)
        {
            AreaHeader = result.Area;
            AreaHeaderVisibility = showArea ? Visibility.Visible : Visibility.Collapsed;
            Title = result.Title;
            Detail = result.Detail;
            FixText = "Fix: " + result.Fix;
            FixVisibility = string.IsNullOrWhiteSpace(result.Fix) || result.Status == CheckStatus.Pass
                ? Visibility.Collapsed
                : Visibility.Visible;

            (BadgeText, BadgeBrush, Background) = result.Status switch
            {
                CheckStatus.Pass => ("OK", Brush(0x16, 0xA3, 0x4A), Brush(0xFF, 0xFF, 0xFF)),
                CheckStatus.Info => ("INFO", Brush(0x64, 0x74, 0x8B), Brush(0xFF, 0xFF, 0xFF)),
                CheckStatus.Warn => ("CHECK", Brush(0xD9, 0x77, 0x06), Brush(0xFF, 0xFB, 0xEB)),
                _ => ("FIX", Brush(0xDC, 0x26, 0x26), Brush(0xFE, 0xF2, 0xF2))
            };
        }

        public string AreaHeader { get; }
        public Visibility AreaHeaderVisibility { get; }
        public string Title { get; }
        public string Detail { get; }
        public string FixText { get; }
        public Visibility FixVisibility { get; }
        public string BadgeText { get; }
        public SolidColorBrush BadgeBrush { get; }
        public SolidColorBrush Background { get; }
    }
}
