using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RentalClient.Windows.Claims;
using RentalClient.Windows.Configuration;
using RentalClient.Windows.Mqtt;
using RentalClient.Windows.Sessions;
using MediaColor = System.Windows.Media.Color;

namespace RentalClient.Windows;

public partial class ActiveSessionOverlayWindow : Window
{
    private const double IdleOpacity = 0.78;
    private const double ExpandedIdleOpacity = 0.9;
    private const double HoverOpacity = 0.98;

    // Segoe MDL2 Assets glyphs that match Windows native window controls.
    // Using these as the expand-toggle gives the overlay a familiar feel
    // (same icons users see on every other Windows window).
    private const string GlyphMaximize = ""; // ChromeMaximize
    private const string GlyphRestore = "";  // ChromeRestore

    // Maximum remaining seconds we've observed in this session. Used to scale
    // the progress bar — without an explicit total, the most recent anchor gives
    // a "you've used X% of your last top-up" feel rather than nothing.
    private long _progressMaxSec;

    // Rainbow LED-frame cue. The overlay ring is 0-thickness at rest; while a
    // coin window is open (claim Granted) it thickens and the gradient spins,
    // matching the lock screen + the coin box's physical RGB frame.
    private const double RainbowFrameThickness = 3;
    private bool _rainbowActive;

    private bool _allowClose;
    private bool _expanded;
    private bool _isPointerOverWindow;
    private bool _initialPositionDone;

    private UpdateBadgeHelper? _updateHandler;
    private CancellationTokenSource? _shutdownTokenSource;

    public ActiveSessionOverlayWindow()
    {
        InitializeComponent();
        SizeChanged += OnOverlaySizeChanged;
        // Stop the animation clock if the widget is hidden (e.g. minimized to
        // tray or the session locks) so it isn't left spinning off-screen.
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                SetRainbow(false);
            }
        };
        SetExpanded(true);
    }

    public event EventHandler? AddTimeClicked;

    public event EventHandler? ClearTimeClicked;

    public event EventHandler? DoneClicked;

    public event EventHandler? MinimizeToTrayClicked;

    public void UpdateState(
        SessionTrackerState state,
        ClaimTrackerState claimState,
        bool canRequestCoinSlot,
        CountdownWarningConfig warningConfig)
    {
        StationText.Text = "TIME REMAINING";
        RemainingText.Text = state.FormatRemaining();

        UpdateProgressFill((long)state.EffectiveRemainingSec);
        UpdateTimeEndingWarning(state, warningConfig);

        var claimInFlight = claimState.Status is ClaimStatus.Pending or ClaimStatus.Granted;

        // Glow the widget frame while the coin window is open — same cue as the
        // lock screen and the coin box's physical RGB frame.
        SetRainbow(claimState.Status == ClaimStatus.Granted);

        if (claimInFlight && !_expanded)
        {
            SetExpanded(true);
        }

        AddTimeButton.IsEnabled = canRequestCoinSlot && !claimInFlight;
        AddTimeButton.Content = claimState.Status == ClaimStatus.Granted
            ? $"Drop ({claimState.GrantedRemainingSec}s)"
            : "Add Time";
        DoneButton.IsEnabled = canRequestCoinSlot && claimState.CanShowDoneButton;
        ClearTimeButton.IsEnabled = canRequestCoinSlot && !claimInFlight;

        UpdateAddTimeAccumulation(claimState);
    }

    private void UpdateTimeEndingWarning(
        SessionTrackerState state,
        CountdownWarningConfig warningConfig)
    {
        if (!warningConfig.Enabled ||
            !state.HasSession ||
            state.Locked ||
            state.EffectiveRemainingSec <= 0 ||
            state.EffectiveRemainingSec > warningConfig.FirstWarningSec)
        {
            TimeEndingWarningBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var remaining = (long)state.EffectiveRemainingSec;
        if (remaining <= warningConfig.FinalCountdownStartSec)
        {
            TimeEndingWarningBanner.Background = new SolidColorBrush(MediaColor.FromRgb(220, 38, 38));
            TimeEndingWarningText.Text = $"TIME ENDS IN {remaining}s";
        }
        else
        {
            TimeEndingWarningBanner.Background = new SolidColorBrush(MediaColor.FromRgb(217, 119, 6));
            TimeEndingWarningText.Text = $"TIME ALMOST UP - {state.FormatRemaining()} LEFT";
        }

        TimeEndingWarningBanner.Visibility = Visibility.Visible;
    }

    private void SetRainbow(bool on)
    {
        if (on == _rainbowActive)
        {
            return;
        }

        _rainbowActive = on;
        var storyboard = (Storyboard)Resources["RainbowSpin"];
        if (on)
        {
            RainbowFrame.BorderThickness = new Thickness(RainbowFrameThickness);
            storyboard.Begin(this, true);
        }
        else
        {
            storyboard.Stop(this);
            RainbowFrame.BorderThickness = new Thickness(0);
        }
    }

    public void UpdateConnectionState(ClientConnectionState state)
    {
        ConnectionDot.Fill = new SolidColorBrush(state.Mode switch
        {
            ClientConnectionMode.OnlineAuthoritative => MediaColor.FromRgb(46, 125, 50),
            ClientConnectionMode.OfflineAutonomous => MediaColor.FromRgb(245, 124, 0),
            _ => MediaColor.FromRgb(198, 40, 40)
        });
        ConnectionDot.ToolTip = state.Detail;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    public void WireUpdateHandler(UpdateBadgeHelper handler, CancellationTokenSource shutdown)
    {
        _updateHandler = handler;
        _shutdownTokenSource = shutdown;
    }

    public void RefreshUpdateBadge(ClaimStatus? claimStatus)
    {
        if (_updateHandler is null)
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var (visible, text, tooltip) = _updateHandler.GetBadgeState(claimStatus);
        if (visible)
        {
            UpdateBadge.Visibility = Visibility.Visible;
            UpdateBadgeText.Text = "UPD";
            UpdateBadge.ToolTip = tooltip;
        }
        else
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnUpdateBadgeClicked(object sender, MouseButtonEventArgs e)
    {
        if (_updateHandler is null || _shutdownTokenSource is null) return;
        await _updateHandler.DownloadAndStageAsync(this, _shutdownTokenSource.Token);
        RefreshUpdateBadge(null);
    }

    private void OnUpdateBadgePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnExpandClicked(object sender, RoutedEventArgs e)
    {
        SetExpanded(!_expanded);
    }

    private void OnAddTimeClicked(object sender, RoutedEventArgs e)
    {
        AddTimeClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearTimeClicked(object sender, RoutedEventArgs e)
    {
        ClearTimeClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        DoneClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
    {
        MinimizeToTrayClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseEntered(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerOverWindow = true;
        Opacity = HoverOpacity;
    }

    private void OnMouseLeft(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerOverWindow = false;
        UpdateOpacity();
    }

    private void OnDragHandleMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Whole-window drag: this handler is wired to the outer Border, so
        // mouse-down anywhere on the card moves the window. Buttons inside
        // intercept their own MouseLeftButtonDown via WPF's mouse-capture
        // machinery (ButtonBase calls CaptureMouse on press), so clicks on
        // Add Time / Done / Clear / Expand / Minimize don't bubble here.
        //
        // Single left-click only — ignore double-clicks so a quick double
        // tap on the card doesn't start a drag.
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.ClickCount == 1)
        {
            try { DragMove(); }
            catch (InvalidOperationException)
            {
                // DragMove can throw if the user releases between WM_LBUTTONDOWN
                // and the call. Safe to swallow — the drag just doesn't start.
            }
        }
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandedSeparator.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = expanded ? GlyphRestore : GlyphMaximize;
        ExpandButton.ToolTip = expanded ? "Restore" : "Expand";

        // With SizeToContent="Height" the window resizes when the expanded panel
        // shows/hides; OnOverlaySizeChanged anchors the bottom-right corner so the
        // card grows upward instead of leaving (or overflowing) dead space.
        UpdateOpacity();
    }

    private void UpdateOpacity()
    {
        if (_isPointerOverWindow)
        {
            Opacity = HoverOpacity;
            return;
        }

        Opacity = _expanded ? ExpandedIdleOpacity : IdleOpacity;
    }

    private void UpdateProgressFill(long remainingSec)
    {
        if (remainingSec > _progressMaxSec)
        {
            // A credit just landed (or first observation). Re-baseline.
            _progressMaxSec = remainingSec;
        }

        if (_progressMaxSec <= 0)
        {
            ProgressFill.Width = 0;
            return;
        }

        // Inner Grid width is OuterBorder.ActualWidth - padding, but the
        // progress strip is full-width inside its own row, so use the strip's
        // grid cell width via the parent.
        var fillRatio = Math.Clamp((double)remainingSec / _progressMaxSec, 0.0, 1.0);
        var trackWidth = Math.Max(0, ActualWidth - OuterBorder.Padding.Left - OuterBorder.Padding.Right - 4);
        ProgressFill.Width = trackWidth * fillRatio;
    }

    private void UpdateAddTimeAccumulation(ClaimTrackerState claimState)
    {
        if (claimState.Status != ClaimStatus.Granted)
        {
            AddTimeAccumulationText.Visibility = Visibility.Collapsed;
            return;
        }

        AddTimeAccumulationText.Text =
            $"₱{claimState.RunningTotalPesos} +{FormatDuration(claimState.RunningTotalSec)}";
        AddTimeAccumulationText.Visibility = Visibility.Visible;
    }

    private static string FormatDuration(long totalSeconds)
    {
        var safeSeconds = Math.Max(0, totalSeconds);
        var hours = safeSeconds / 3600;
        var minutes = safeSeconds % 3600 / 60;
        var seconds = safeSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialPositionDone)
        {
            // First real layout — SizeToContent has computed our height, so
            // ActualWidth/ActualHeight are valid. Anchor bottom-right. (We do
            // this unconditionally: MainWindow never pre-places the widget, and
            // by the time SizeChanged fires WPF has already assigned a non-zero
            // default position, so a Left==0/Top==0 guard would strand the card
            // mid-screen.)
            RepositionBottomRight();
            _initialPositionDone = true;
            return;
        }

        // Expand/collapse changes our height. Keep the BOTTOM edge fixed so the
        // card grows upward from its corner instead of shifting its bottom.
        if (e.HeightChanged)
        {
            var newTop = Top - (e.NewSize.Height - e.PreviousSize.Height);
            Top = Math.Max(SystemParameters.WorkArea.Top, newTop);
        }
    }

    private void RepositionBottomRight()
    {
        Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            MinimizeToTrayClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnClosing(e);
    }
}
