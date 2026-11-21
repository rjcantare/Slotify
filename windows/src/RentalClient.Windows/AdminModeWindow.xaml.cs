using System.Windows;
using System.Windows.Input;
using RentalClient.Windows.Claims;

namespace RentalClient.Windows;

/// <summary>
/// Compact admin window shown after the operator enters the admin password on the
/// lock overlay. Replaces MainWindow for daily admin tasks — no station ID, no broker
/// URL, no raw session diagnostics. S55 addition.
/// </summary>
public partial class AdminModeWindow : Window
{
    private UpdateBadgeHelper? _updateHandler;
    private CancellationTokenSource? _shutdown;

    public AdminModeWindow()
    {
        InitializeComponent();
    }

    /// <summary>Fires when the operator clicks Return to Lock.</summary>
    public event EventHandler? ReturnToLockClicked;

    public void WireUpdateHandler(UpdateBadgeHelper handler, CancellationTokenSource shutdown)
    {
        _updateHandler = handler;
        _shutdown = shutdown;
    }

    public void RefreshUpdateBadge(ClaimStatus? claimStatus)
    {
        if (_updateHandler is null)
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
            NoUpdateText.Visibility = Visibility.Visible;
            return;
        }

        var (visible, text, tooltip) = _updateHandler.GetBadgeState(claimStatus);
        if (visible)
        {
            UpdateBadge.Visibility = Visibility.Visible;
            UpdateBadgeText.Text = text;
            UpdateBadge.ToolTip = tooltip;
            NoUpdateText.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
            NoUpdateText.Visibility = Visibility.Visible;
        }
    }

    private async void OnUpdateBadgeClicked(object sender, MouseButtonEventArgs e)
    {
        if (_updateHandler is null || _shutdown is null) return;
        await _updateHandler.DownloadAndStageAsync(this, _shutdown.Token);
        RefreshUpdateBadge(null);
    }

    private void OnReturnToLockClicked(object sender, RoutedEventArgs e)
    {
        ReturnToLockClicked?.Invoke(this, EventArgs.Empty);
    }
}
