using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;
using RentalClient.Windows.Audio;
using RentalClient.Windows.Claims;
using RentalClient.Windows.Configuration;
using RentalClient.Windows.Input;
using RentalClient.Windows.Locking;
using RentalClient.Windows.Mqtt;
using RentalShared.Mqtt;

namespace RentalClient.Windows;

public partial class LockOverlayWindow : Window, IDisposable
{
    // Server-published admin password config. Null until the server's retained
    // tablet-admin config arrives. Once set, the entered password is verified
    // against the salted hash instead of the fallback.
    private TabletAdminConfigMessage? _adminConfig;
    private bool _allowFallbackAdminPassword = true;

    private readonly KeyboardBlocker _blocker = new();
    private readonly AudioMuter _muter = new();
    private ClientConnectionState _connectionState = new(
        ClientConnectionMode.OfflineUntrusted,
        "OFFLINE",
        "Not connected.",
        false);

    // The minimum total pesos required for the locked Insert Coin rule to
    // credit any time. Below this, accumulated pesos are forfeited at TTL.
    // MainWindow sets this from the retained rate sheet (S21b.InsertCoinRules);
    // we use it to surface a "need ₱X more to unlock" hint during sub-threshold
    // accumulation in a claim window.
    private int _lockedMinimumPesos;

    // Tracks whether the user has already clicked DONE in the current Granted
    // window. OnDoneClicked sets this true; UpdateClaimState resets it false
    // whenever the claim leaves the Granted state (a new window starts on the
    // next Granted transition). Prevents both:
    //   - Stale "disabled" surviving into the next claim window
    //     (LockOverlayWindow's UpdateClaimState only touched Visibility, so
    //     after the first DONE click ever, IsEnabled stayed false forever).
    //     Reported 2026-05-23 known-issues.md #1.
    //   - Double-click during the brief window between click and server
    //     response, which would re-publish claim/end and risk a duplicate
    //     publish race.
    private bool _doneClickedInCurrentWindow;

    // Rainbow LED-frame cue state. The border is 0-thickness at rest; while a
    // coin window is open (claim Granted) it thickens and the gradient spins.
    private const double RainbowFrameThickness = 16;
    private bool _rainbowActive;

    public LockOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ResnapToMonitor();
        Loaded += (_, _) => ResnapToMonitor();
        IsVisibleChanged += OnIsVisibleChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _blocker.Dispose();
            _muter.Dispose();
        };
    }

    public event EventHandler? InsertCoinClicked;

    // S18b: user clicked DONE inside the multi-coin window.
    public event EventHandler? DoneClicked;

    public event EventHandler? AdminUnlocked;

    private UpdateBadgeHelper? _updateHandler;
    private CancellationTokenSource? _shutdownTokenSource;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Fullscreen games can leave WPF with stale maximized bounds after a
        // resolution switch. Re-snap after Windows finishes applying the mode.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ResnapToMonitor, DispatcherPriority.ApplicationIdle);
            return;
        }

        Dispatcher.BeginInvoke(ResnapToMonitor, DispatcherPriority.ApplicationIdle);
    }

    private void ResnapToMonitor()
    {
        if (IsVisible)
        {
            LockOverlayEnforcer.BringToFront(this);
        }
    }

    /// <summary>
    /// Receives the server-published admin password config from the retained
    /// rental/config/tablet-admin topic. Once set, the admin unlock dialog
    /// verifies against the salted hash instead of the hardcoded fallback.
    /// </summary>
    public void UpdateAdminConfig(
        TabletAdminConfigMessage? config,
        bool allowFallbackAdminPassword)
    {
        _adminConfig = config;
        _allowFallbackAdminPassword = allowFallbackAdminPassword;
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
            UpdateBadgeText.Text = text;
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

    public void UpdateState(string stationId, long remainingSec)
    {
        StationLabel.Text = string.IsNullOrEmpty(stationId)
            ? string.Empty
            : $"Station {stationId}";

        RemainingText.Text = FormatDuration(remainingSec);
    }

    public void UpdateRateText(string text)
    {
        RateInfoText.Text = text;
        RateInfoText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Set the ₱ minimum for the locked Insert Coin rule. MainWindow calls this
    /// whenever a new rate sheet is published; the running-total display uses it
    /// to nudge customers below the threshold ("need ₱X more to unlock") so
    /// sub-threshold drops aren't a silent money sink.
    /// </summary>
    public void UpdateLockedMinimumPesos(int minPesos)
    {
        _lockedMinimumPesos = Math.Max(0, minPesos);
    }

    public void UpdateClaimState(ClaimTrackerState state)
    {
        // Mirror the physical coin-box LED frame: glow while the coin window is
        // open, dark otherwise. Driven purely off claim state, not a timer.
        SetRainbow(state.Status == ClaimStatus.Granted);

        // DONE is allowed with no coins dropped yet (so the user can close an
        // empty window) and once the starter minimum is reached. It is removed
        // only when coins HAVE been dropped but are still below the minimum, so
        // a sub-minimum amount isn't accidentally forfeited. Mirrors the Android
        // lock screen's belowStarterMinimum guard (the RunningTotalPesos > 0
        // check is what allows DONE at zero coins).
        var canDone = state.CanShowDoneButton;
        if (canDone && state.Status == ClaimStatus.Granted &&
            state.RunningTotalPesos > 0 &&
            _lockedMinimumPesos > 0 && state.RunningTotalPesos < _lockedMinimumPesos)
        {
            canDone = false;
        }
        DoneButton.Visibility = canDone
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (state.Status != ClaimStatus.Granted)
        {
            _doneClickedInCurrentWindow = false;
        }
        DoneButton.IsEnabled = canDone && !_doneClickedInCurrentWindow;

        // Tablet-style live accumulation for the locked Insert Coin window: a big
        // realtime time total (driven by RunningTotalSec) above the peso total,
        // shown for the whole Granted window from zero coins. The lock overlay
        // only appears when the station is locked, so a Granted claim here is
        // always a locked Insert Coin window (Add Time uses the active overlay).
        // Mirrors the Android lock screen. The session-time line is hidden during
        // the window so it doesn't show a redundant/diverging 00:00:00.
        if (state.Status == ClaimStatus.Granted)
        {
            var pesos = state.RunningTotalPesos;
            var hasMinimum = _lockedMinimumPesos > 0;
            var belowMinimum = hasMinimum && pesos > 0 && pesos < _lockedMinimumPesos;
            var hint = pesos <= 0
                ? (hasMinimum ? $"Insert \u20b1{_lockedMinimumPesos} or more to unlock" : "Drop coins to start")
                : belowMinimum
                    ? $"Need \u20b1{_lockedMinimumPesos - pesos} more to unlock"
                    : "Ready to start";

            var timeBrush = new SolidColorBrush(MediaColor.FromRgb(0xF4, 0xC0, 0x4C)); // amber: pending
            var pesoBrush = new SolidColorBrush(
                belowMinimum
                    ? MediaColor.FromRgb(0xFF, 0xF8, 0x71)              // red: below minimum
                    : (hasMinimum && pesos >= _lockedMinimumPesos)
                        ? MediaColor.FromRgb(0x86, 0xEF, 0xAC)          // green: ready
                        : MediaColor.FromRgb(0xF4, 0xC0, 0x4C));        // amber: zero / no minimum

            RunningTotalText.Inlines.Clear();
            RunningTotalText.Inlines.Add(new Run(FormatDuration(state.RunningTotalSec))
            {
                FontSize = 72,
                FontWeight = FontWeights.Bold,
                Foreground = timeBrush
            });
            RunningTotalText.Inlines.Add(new LineBreak());
            RunningTotalText.Inlines.Add(new Run($"\u20b1{pesos}")
            {
                FontSize = 44,
                FontWeight = FontWeights.Bold,
                Foreground = pesoBrush
            });
            RunningTotalText.Inlines.Add(new LineBreak());
            RunningTotalText.Inlines.Add(new Run(hint)
            {
                FontSize = 24,
                Foreground = pesoBrush
            });
            RunningTotalText.Visibility = Visibility.Visible;
            RemainingText.Visibility = Visibility.Collapsed;
        }
        else
        {
            RunningTotalText.Inlines.Clear();
            RunningTotalText.Visibility = Visibility.Collapsed;
            RemainingText.Visibility = Visibility.Visible;
        }

        switch (state.Status)
        {
            case ClaimStatus.Idle:
                InsertCoinButton.IsEnabled = _connectionState.CanRequestCoinSlot;
                InsertCoinButton.Content = _connectionState.CanRequestCoinSlot ? "INSERT COIN" : "OFFLINE";
                ClaimStatusText.Text = _connectionState.CanRequestCoinSlot
                    ? string.Empty
                    : "Please wait for connection.";
                break;

            case ClaimStatus.Pending:
                InsertCoinButton.IsEnabled = false;
                InsertCoinButton.Content = "REQUESTING...";
                ClaimStatusText.Text = "Requesting coin slot...";
                break;

            case ClaimStatus.Granted:
                InsertCoinButton.IsEnabled = false;
                InsertCoinButton.Content = $"DROP COINS ({state.GrantedRemainingSec}s)";
                ClaimStatusText.Text = state.CanShowDoneButton
                    ? "Drop coins now. Press DONE when finished."
                    : "Drop coins now.";
                break;

            case ClaimStatus.Denied:
                InsertCoinButton.IsEnabled =
                    _connectionState.CanRequestCoinSlot && state.DenyReason != "station_unknown"
                    && state.DenyReason != "abuse_ban";
                InsertCoinButton.Content = state.DenyReason == "abuse_ban"
                    ? "RESTRICTED"
                    : _connectionState.CanRequestCoinSlot ? "INSERT COIN" : "OFFLINE";
                ClaimStatusText.Text = state.DenyReason switch
                {
                    "slot_in_use" => "Slot in use. Please wait.",
                    "no_acceptor_online" => "Coin acceptor offline. Please try again later.",
                    "station_unknown" => "This station is not set up. Please ask staff.",
                    "abuse_ban" => BuildBanMessage(state.BanUntil),
                    _ => "Coin slot unavailable. Please ask staff."
                };
                break;

            case ClaimStatus.TimedOut:
                InsertCoinButton.IsEnabled = _connectionState.CanRequestCoinSlot;
                InsertCoinButton.Content = _connectionState.CanRequestCoinSlot ? "INSERT COIN" : "OFFLINE";
                ClaimStatusText.Text = !_connectionState.CanRequestCoinSlot
                    ? "Please wait for connection."
                    : state.RunningTotalPesos > 0 && _lockedMinimumPesos > 0 &&
                      state.RunningTotalPesos < _lockedMinimumPesos
                    ? "Call staff — you entered less than the minimum amount."
                    : state.RunningTotalPesos > 0
                    ? "Window closed. Waiting for server confirmation..."
                    : "No coin detected — try again.";
                break;
        }
    }

    public void UpdateConnectionState(ClientConnectionState state)
    {
        _connectionState = state;
        ConnectionBadge.ToolTip = state.Detail;
        ConnectionBadge.Fill = new SolidColorBrush(state.Mode switch
        {
            ClientConnectionMode.OnlineAuthoritative => MediaColor.FromRgb(46, 125, 50),
            ClientConnectionMode.OfflineAutonomous => MediaColor.FromRgb(245, 124, 0),
            _ => MediaColor.FromRgb(183, 28, 28)
        });
    }

    public void UpdateAutoShutdownCountdown(int? remainingSec)
    {
        if (remainingSec is null)
        {
            AutoShutdownText.Visibility = Visibility.Collapsed;
            AutoShutdownText.Text = string.Empty;
            return;
        }

        AutoShutdownText.Text = $"Auto shutdown in {FormatMinutesSeconds(remainingSec.Value)}";
        AutoShutdownText.Visibility = Visibility.Visible;
    }

    private static string BuildBanMessage(string? banUntilUtc)
    {
        if (banUntilUtc is null)
        {
            return "Too many empty attempts. Please wait.";
        }

        if (!DateTimeOffset.TryParse(banUntilUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var banUntil))
        {
            return "Too many empty attempts. Please wait.";
        }

        var remaining = banUntil - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "Restriction expired. Try again.";
        }

        if (remaining.TotalMinutes >= 1)
        {
            var mins = (int)Math.Ceiling(remaining.TotalMinutes);
            return $"Too many empty attempts. Wait {mins} minute{(mins > 1 ? "s" : "")}.";
        }

        var secs = (int)Math.Ceiling(remaining.TotalSeconds);
        return $"Too many empty attempts. Wait {secs} second{(secs > 1 ? "s" : "")}.";
    }

    private void OnInsertCoinClicked(object sender, RoutedEventArgs e)
    {
        InsertCoinClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        // Disable both buttons during the brief window while MainWindow publishes
        // claim/end. Locked Insert Coin applies on close; Add Time applies per coin.
        _doneClickedInCurrentWindow = true;
        DoneButton.IsEnabled = false;
        InsertCoinButton.IsEnabled = false;
        ClaimStatusText.Text = "Closing window...";

        DoneClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnAdminClicked(object sender, RoutedEventArgs e)
    {
        if (PromptForAdminPassword())
        {
            ClaimStatusText.Text = "Admin unlocked.";
            AdminUnlocked?.Invoke(this, EventArgs.Empty);
            return;
        }

        ClaimStatusText.Text = "Incorrect admin password.";
    }

    private bool PromptForAdminPassword()
    {
        var passwordBox = new PasswordBox
        {
            Width = 260,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var accepted = false;
        var dialog = new Window
        {
            Title = "Admin unlock",
            Owner = this,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
            ShowInTaskbar = false,
            Content = BuildAdminPromptContent(passwordBox, () =>
            {
                accepted = true;
            })
        };

        dialog.Loaded += (_, _) => passwordBox.Focus();
        dialog.ShowDialog();

        return accepted && VerifyAdminPassword(passwordBox.Password);
    }

    private bool VerifyAdminPassword(string entered)
    {
        return AdminPasswordVerifier.Verify(
            entered,
            _adminConfig,
            _allowFallbackAdminPassword);
    }

    private static FrameworkElement BuildAdminPromptContent(
        PasswordBox passwordBox,
        Action accept)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(18)
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Enter admin password",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(passwordBox);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var unlockButton = new System.Windows.Controls.Button
        {
            Content = "Unlock",
            MinWidth = 76,
            IsDefault = true
        };

        cancelButton.Click += (_, _) => Window.GetWindow(cancelButton)?.Close();
        unlockButton.Click += (_, _) =>
        {
            accept();
            Window.GetWindow(unlockButton)?.Close();
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(unlockButton);
        panel.Children.Add(buttons);

        return panel;
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

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            _blocker.Install();
            _muter.Mute();
        }
        else
        {
            _blocker.Uninstall();
            _muter.Unmute();
            // Don't leave the animation clock running on a hidden window.
            SetRainbow(false);
        }
    }

    public void Dispose()
    {
        _blocker.Dispose();
        _muter.Dispose();
    }

    private static string FormatDuration(long totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    private static string FormatMinutesSeconds(int totalSeconds)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes:D2}:{seconds:D2}";
    }
}
