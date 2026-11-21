using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;
using WpfMessageBox = System.Windows.MessageBox;
using RentalClient.Windows.Audio;
using RentalClient.Windows.Claims;
using RentalClient.Windows.Configuration;
using RentalClient.Windows.Credits;
using RentalClient.Windows.Locking;
using RentalClient.Windows.Mqtt;
using RentalClient.Windows.PcPolicy;
using RentalClient.Windows.Sessions;
using RentalShared.Mqtt;
using RentalShared.Updates;

namespace RentalClient.Windows;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SessionTracker _sessionTracker = new();
    private readonly ClaimTracker _claimTracker = new();
    private readonly CreditAckService _creditAck = new();
    private readonly WindowsAppCloser _appCloser = new();
    private readonly PcIdleShutdownCountdownTracker _idleShutdownCountdown = new();

    private ClientConfig? _config;
    private AdminPasswordConfigCache? _adminPasswordCache;
    private CountdownWarningService? _countdownWarnings;
    private StationMqttClient? _mqtt;
    private LockOverlayWindow? _overlay;
    private ActiveSessionOverlayWindow? _activeOverlay;
    private AdminModeWindow? _adminWindow;
    private Forms.NotifyIcon? _trayIcon;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _reconnectTimer;
    private DispatcherTimer? _toastHideTimer;
    private RateConfigMessage? _rateConfig;
    private TabletAdminConfigMessage? _adminConfig;
    private bool _allowFallbackAdminPassword = true;
    private PcPolicyConfigMessage _pcPolicyConfig = new();
    private UpdateBadgeHelper? _updateHandler;
    private AdminCommandHandler? _adminCommandHandler;
    private MqttConnectionStatus _lastMqttStatus = new(false, "Not connected");
    private DateTimeOffset? _disconnectedAtUtc;
    private bool _connectInFlight;
    private bool _activeOverlayHiddenToTray;
    private bool _adminLockBypass;
    private string? _lockActionsSessionId;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // --maintenance flag skips the lock overlay, keyboard blocker, and
        // audio mute so the operator can debug a station without the kiosk.
        _adminLockBypass = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--maintenance", StringComparison.OrdinalIgnoreCase));

        try
        {
            _config = ClientConfig.LoadFromDisk();
        }
        catch (Exception ex)
        {
            ApplyStatus(new MqttConnectionStatus(false, "Failed to load appsettings.json", ex.Message));
            return;
        }

        TitleText.Text = $"Rental Client — {_config.StationId}";
        StationText.Text = $"Station: {_config.StationId} ({_config.DeviceType})";
        BrokerText.Text = $"Broker: {_config.Broker.Host}:{_config.Broker.Port}";
        _adminPasswordCache = new AdminPasswordConfigCache(_config.StationId);
        _adminConfig = _adminPasswordCache.Load();
        _allowFallbackAdminPassword = _adminConfig is null;
        EnsureTrayIcon();
        var disableOverlay = _config.Debug.DisableLockOverlay;
        DebugModeText.Visibility = disableOverlay ? Visibility.Visible : Visibility.Collapsed;
        if (_adminLockBypass)
        {
            DiagnosticPanel.Visibility = Visibility.Visible;
            ButtonPanel.Visibility = Visibility.Visible;
        }
        InsertCoinMainButton.Visibility = Visibility.Collapsed;
        AddTimeMainButton.Visibility = Visibility.Collapsed;
        DoneMainButton.Visibility = Visibility.Collapsed;
        _countdownWarnings = new CountdownWarningService(
            _config.CountdownWarning,
            new SystemCountdownWarningSoundPlayer());

        _updateHandler = new UpdateBadgeHelper();

        _mqtt = new StationMqttClient(_config);
        _adminCommandHandler = new AdminCommandHandler(_mqtt, _config.StationId);
        _mqtt.StatusChanged += OnMqttStatusChanged;
        _mqtt.SessionReceived += OnSessionReceived;
        _mqtt.ClaimResultReceived += OnClaimResultReceived;
        _mqtt.CreditReceived += OnCreditReceived;
        _mqtt.RateConfigReceived += OnRateConfigReceived;
        _mqtt.TabletAdminConfigReceived += OnTabletAdminConfigReceived;
        _mqtt.PcPolicyConfigReceived += OnPcPolicyConfigReceived;
        _mqtt.UpdateManifestReceived += OnUpdateManifestReceived;
        _mqtt.AdminCommandReceived += OnAdminCommandReceived;

        _sessionTracker.StateChanged += OnSessionTrackerStateChanged;
        _claimTracker.StateChanged += OnClaimTrackerStateChanged;
        _creditAck.NewCreditApplied += OnNewCreditApplied;

        // 1 Hz UI refresh — drives both the session remaining countdown and the
        // granted-claim window countdown between server transitions. Also runs the
        // client-side claim timeout check: if a granted claim has been open >= its
        // window without a coin/credit landing, mark it TimedOut so the button
        // re-enables for another try.
        _uiTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                var claimState = _claimTracker.GetCurrentState();
                if (claimState.Status == ClaimStatus.Granted &&
                    claimState.ElapsedSec >= claimState.WindowSec)
                {
                    _claimTracker.MarkTimedOut();
                    claimState = _claimTracker.GetCurrentState();
                }

                // Auto-reset denied abuse_ban when the ban period has expired so
                // the Insert Coin button re-enables without manual retry.
                if (claimState.Status == ClaimStatus.Denied &&
                    claimState.DenyReason == "abuse_ban" &&
                    claimState.BanUntil is not null &&
                    DateTimeOffset.TryParse(claimState.BanUntil, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var banUntil) &&
                    DateTimeOffset.UtcNow >= banUntil)
                {
                    _claimTracker.Reset();
                    claimState = _claimTracker.GetCurrentState();
                }

                ApplySessionState(_sessionTracker.GetCurrentState());
                ApplyClaimState(claimState);
            },
            Dispatcher);
        _uiTimer.Start();

        _reconnectTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(Math.Max(1, _config.Connection.ReconnectIntervalSec)),
            DispatcherPriority.Background,
            (_, _) =>
            {
                ApplyConnectionState(_sessionTracker.GetCurrentState());
                if (_mqtt is not null && !_mqtt.IsConnected && !_connectInFlight)
                {
                    _ = ConnectAsync();
                }
            },
            Dispatcher);
        _reconnectTimer.Start();

        ApplySessionState(_sessionTracker.GetCurrentState());
        ApplyClaimState(_claimTracker.GetCurrentState());

        // S55: hide the diagnostic MainWindow at boot. Customers only see the
        // LockOverlay or ActiveSessionOverlay. The operator can reveal MainWindow
        // by passing --maintenance at launch or via the tray icon.
        if (!_adminLockBypass)
        {
            WindowState = WindowState.Minimized;
            Hide();
        }

        // Let WPF paint the local fail-safe lock before any MQTT/discovery wait.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await ConnectAsync();
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested) return;

        if (_mqtt is null)
        {
            OnLoaded(sender, new RoutedEventArgs());
            return;
        }

        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_mqtt is null) return;
        if (_connectInFlight) return;

        _connectInFlight = true;
        try
        {
            await _mqtt.ConnectAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ApplyStatus(new MqttConnectionStatus(false, "Could not connect to broker.", ex.Message));
        }
        finally
        {
            _connectInFlight = false;
        }
    }

    private void OnMqttStatusChanged(object? sender, MqttConnectionStatus status)
    {
        Dispatcher.Invoke(() => ApplyStatus(status));
    }

    private void OnSessionReceived(object? sender, SessionMessage message)
    {
        _sessionTracker.Apply(message);
    }

    private void OnClaimResultReceived(object? sender, ClaimResultMessage message)
    {
        _claimTracker.Apply(message);
    }

    private async void OnCreditReceived(object? sender, CreditMessage message)
    {
        if (_mqtt is null) return;

        try
        {
            await _creditAck.ProcessAsync(
                message,
                seqNo => _mqtt.PublishCreditAckAsync(seqNo, _shutdown.Token),
                _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => ApplyStatus(new MqttConnectionStatus(
                _mqtt.IsConnected,
                "Credit ack publish failed.",
                ex.Message)));
        }
    }

    private void OnRateConfigReceived(object? sender, RateConfigMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            _rateConfig = message;
            _overlay?.UpdateRateText(BuildLockedRateText(_sessionTracker.GetCurrentState()));
            _overlay?.UpdateLockedMinimumPesos(GetLockedMinimumPesos(_sessionTracker.GetCurrentState()));
        });
    }

    private void OnTabletAdminConfigReceived(object? sender, TabletAdminConfigMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            _adminConfig = message;
            _allowFallbackAdminPassword = false;
            try
            {
                _adminPasswordCache?.Save(message);
            }
            catch
            {
                // Current retained config still works for this run; caching is
                // only needed for later offline admin unlocks.
            }

            _overlay?.UpdateAdminConfig(message, _allowFallbackAdminPassword);
        });
    }

    private void OnPcPolicyConfigReceived(object? sender, PcPolicyConfigMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            _pcPolicyConfig = message;
            _idleShutdownCountdown.Reset();
        });
    }

    private void OnUpdateManifestReceived(object? sender, UpdateManifest message)
    {
        Dispatcher.Invoke(() =>
        {
            _updateHandler?.SetManifest(message);
            RefreshAllUpdateBadges();
        });
    }

    // S49: server-issued restart/reboot/shutdown. HandleAsync sends the ack
    // first and then triggers the OS-level action; we don't await it from
    // the MQTT event because the handler intentionally kills the process.
    private void OnAdminCommandReceived(object? sender, AdminCommandMessage message)
    {
        if (_adminCommandHandler is null) return;
        _ = _adminCommandHandler.HandleAsync(message, _shutdown.Token);
    }

    // The pesos threshold below which an Insert Coin claim TTL-expires with zero
    // credit. Drives the "need ₱X more to unlock" warning on the lock overlay.
    private int GetLockedMinimumPesos(SessionTrackerState state)
    {
        var deviceType = !string.IsNullOrWhiteSpace(state.DeviceType)
            ? state.DeviceType
            : _config?.DeviceType ?? "pc";
        var rule = TryGetInsertCoinRule(deviceType);
        return rule?.StarterMaxPesos ?? 7;
    }

    private void OnNewCreditApplied(object? sender, CreditMessage message)
    {
        var claimState = _claimTracker.GetCurrentState();
        var sessionState = _sessionTracker.GetCurrentState();
        var inClaimWindow = claimState.Status == ClaimStatus.Granted;
        ClaimTrackerState? updatedClaimState = null;
        if (inClaimWindow)
        {
            _claimTracker.AddRunningCoin(message.SecondsToAdd, message.Denomination);
            updatedClaimState = _claimTracker.GetCurrentState();
        }

        Dispatcher.Invoke(() =>
        {
            var prefix = inClaimWindow && sessionState.Locked
                ? "Accumulated"
                : "Added";
            var label = message.Denomination is int peso
                ? $"+{message.SecondsToAdd}s added (₱{peso})"
                : $"+{message.SecondsToAdd}s added ({message.Source})";
            label = message.Denomination is int pesoValue
                ? $"{prefix}: {FormatDuration(message.SecondsToAdd)} (\u20b1{pesoValue})"
                : $"{prefix}: {FormatDuration(message.SecondsToAdd)} ({message.Source})";
            if (inClaimWindow && sessionState.Locked && updatedClaimState is not null)
            {
                label = $"Accumulated: {FormatDuration(updatedClaimState.RunningTotalSec)} (\u20b1{updatedClaimState.RunningTotalPesos})";
            }
            ShowToast(label);
        });
    }

    private async void OnDoneClicked(object? sender, EventArgs e)
    {
        if (_mqtt is null) return;
        if (_shutdown.IsCancellationRequested) return;
        if (!_mqtt.IsConnected)
        {
            ApplyConnectionState(_sessionTracker.GetCurrentState());
            return;
        }

        var claimState = _claimTracker.GetCurrentState();
        if (claimState.Status != ClaimStatus.Granted) return;
        if (string.IsNullOrEmpty(claimState.RequestId) || string.IsNullOrEmpty(claimState.ClaimId))
        {
            // Tracker missing required ids — shouldn't happen in Granted. Re-render
            // to recover the UI.
            ApplyClaimState(_claimTracker.GetCurrentState());
            return;
        }

        try
        {
            DoneMainButton.IsEnabled = false;
            InsertCoinMainButton.IsEnabled = false;
            AddTimeMainButton.IsEnabled = false;
            await _mqtt.PublishClaimEndAsync(claimState.RequestId, claimState.ClaimId, _shutdown.Token);
            _claimTracker.Reset();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Publish failed. Re-render the overlay so DONE button comes back.
            ApplyStatus(new MqttConnectionStatus(
                _mqtt.IsConnected,
                "Failed to publish claim end.",
                ex.Message));
            ApplyClaimState(_claimTracker.GetCurrentState());
        }
    }

    private void OnSessionTrackerStateChanged(object? sender, SessionTrackerState state)
    {
        Dispatcher.Invoke(() => ApplySessionState(state));
    }

    private void OnClaimTrackerStateChanged(object? sender, ClaimTrackerState state)
    {
        Dispatcher.Invoke(() => ApplyClaimState(state));
    }

    private void OnMainInsertCoinClicked(object sender, RoutedEventArgs e)
    {
        OnInsertCoinClicked(sender, e);
    }

    private void OnMainAddTimeClicked(object sender, RoutedEventArgs e)
    {
        OnInsertCoinClicked(sender, e);
    }

    private void OnMainDoneClicked(object sender, RoutedEventArgs e)
    {
        DoneMainButton.IsEnabled = false;
        InsertCoinMainButton.IsEnabled = false;
        AddTimeMainButton.IsEnabled = false;
        OnDoneClicked(sender, e);
    }

    private async void OnClearTimeClicked(object? sender, EventArgs e)
    {
        if (_mqtt is null) return;
        if (_shutdown.IsCancellationRequested) return;
        if (!_mqtt.IsConnected)
        {
            ApplyConnectionState(_sessionTracker.GetCurrentState());
            return;
        }

        var state = _sessionTracker.GetCurrentState();
        if (!state.HasSession || state.Locked || state.EffectiveRemainingSec <= 0)
        {
            return;
        }

        var owner = _activeOverlay is not null ? (Window)_activeOverlay : this;
        var confirm = WpfMessageBox.Show(
            owner,
            "Clear the remaining time and lock this PC?",
            "Clear Time",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var requestId = $"clear-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            await _mqtt.PublishSessionClearAsync(requestId, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ApplyStatus(new MqttConnectionStatus(
                _mqtt.IsConnected,
                "Failed to publish clear-time request.",
                ex.Message));
        }
    }

    private async void OnInsertCoinClicked(object? sender, EventArgs e)
    {
        if (_mqtt is null) return;
        if (_shutdown.IsCancellationRequested) return;
        if (!_mqtt.IsConnected)
        {
            ApplyConnectionState(_sessionTracker.GetCurrentState());
            return;
        }

        // Time-based requestId — uniqueness within this client's lifetime is sufficient
        // since the server scopes claims per stationId and only accepts one pending at a time.
        var currentClaim = _claimTracker.GetCurrentState();
        if (currentClaim.Status is ClaimStatus.Pending or ClaimStatus.Granted)
        {
            return;
        }

        var requestId = $"r-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _claimTracker.StartPending(requestId);

        try
        {
            await _mqtt.PublishClaimRequestAsync(requestId, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Roll back the tracker so the button re-enables.
            _claimTracker.Reset();
            ApplyStatus(new MqttConnectionStatus(
                _mqtt.IsConnected,
                "Failed to publish claim request.",
                ex.Message));
        }
    }

    private void ApplyStatus(MqttConnectionStatus status)
    {
        _lastMqttStatus = status;
        if (status.IsConnected)
        {
            _disconnectedAtUtc = null;
        }
        else
        {
            _disconnectedAtUtc ??= DateTimeOffset.UtcNow;
        }

        ApplyConnectionState(_sessionTracker.GetCurrentState());
        StatusText.Text = status.Message;
        DetailText.Text = status.Detail ?? string.Empty;
        RefreshAllUpdateBadges();
    }

    private void RefreshAllUpdateBadges()
    {
        var claimStatus = _claimTracker.GetCurrentState().Status;
        _overlay?.RefreshUpdateBadge(claimStatus);
        _activeOverlay?.RefreshUpdateBadge(claimStatus);
        _adminWindow?.RefreshUpdateBadge(claimStatus);
    }

    private void ApplyConnectionState(SessionTrackerState? sessionState = null)
    {
        var state = GetConnectionState(sessionState ?? _sessionTracker.GetCurrentState());

        StatusIndicator.Fill = new SolidColorBrush(state.Mode switch
        {
            ClientConnectionMode.OnlineAuthoritative => MediaColor.FromRgb(46, 125, 50),
            ClientConnectionMode.OfflineAutonomous => MediaColor.FromRgb(245, 124, 0),
            _ => MediaColor.FromRgb(198, 40, 40)
        });

        ConnectionModeText.Text = state.Label;
        ConnectionModeText.ToolTip = state.Detail;
        _overlay?.UpdateConnectionState(state);
        _activeOverlay?.UpdateConnectionState(state);
    }

    private ClientConnectionState GetConnectionState(SessionTrackerState state)
    {
        if (_mqtt?.IsConnected == true && _lastMqttStatus.IsConnected)
        {
            return new ClientConnectionState(
                ClientConnectionMode.OnlineAuthoritative,
                "ONLINE",
                "Server reachable. Session state is authoritative.",
                true);
        }

        var disconnectedAt = _disconnectedAtUtc ?? DateTimeOffset.UtcNow;
        var offlineFor = DateTimeOffset.UtcNow - disconnectedAt;
        var untrustedAfter = TimeSpan.FromSeconds(Math.Max(
            1,
            _config?.Connection.OfflineUntrustedAfterSec ?? 120));
        var hasSpendableServerTime =
            state.HasSession && !state.Locked && state.EffectiveRemainingSec > 0;

        if (hasSpendableServerTime && offlineFor < untrustedAfter)
        {
            return new ClientConnectionState(
                ClientConnectionMode.OfflineAutonomous,
                "AUTONOMOUS",
                $"Server unreachable for {(int)offlineFor.TotalSeconds}s. Local countdown continues; sync on reconnect.",
                false);
        }

        return new ClientConnectionState(
            ClientConnectionMode.OfflineUntrusted,
            "UNTRUSTED",
            $"Server unreachable for {(int)offlineFor.TotalSeconds}s. Waiting for server sync.",
            false);
    }

    private void ApplySessionState(SessionTrackerState state)
    {
        ApplyConnectionState(state);
        _countdownWarnings?.Update(state);

        if (!state.HasSession)
        {
            SessionText.Text = "Session: LOCKED (waiting for server)";
            HideActiveOverlay(clearTrayHidden: true);
            ShowOverlay(BuildStartupLockedState());
            return;
        }

        SessionText.Text = state.Locked
            ? "Session: LOCKED"
            : $"Session: {state.FormatRemaining()} remaining";

        if (state.Locked)
        {
            HideActiveOverlay(clearTrayHidden: true);
            RunLockedTransitionActions(state);
            ShowOverlay(state);
        }
        else
        {
            _adminLockBypass = false;
            _lockActionsSessionId = null;
            HideOverlay();
            if (state.EffectiveRemainingSec > 0)
            {
                if (ShouldShowTimeEndingWarning(state))
                {
                    _activeOverlayHiddenToTray = false;
                }

                ShowActiveOverlay(state, _claimTracker.GetCurrentState());
            }
            else
            {
                HideActiveOverlay(clearTrayHidden: true);
            }
        }
    }

    private void ApplyClaimState(ClaimTrackerState state)
    {
        var sessionState = _sessionTracker.GetCurrentState();
        var connectionState = GetConnectionState(sessionState);
        var canStartClaim = connectionState.CanRequestCoinSlot &&
                            state.Status is ClaimStatus.Idle or ClaimStatus.Denied or ClaimStatus.TimedOut;
        var hasActiveTime =
            sessionState.HasSession &&
            !sessionState.Locked &&
            sessionState.EffectiveRemainingSec > 0;
        var claimInFlight = state.Status is ClaimStatus.Pending or ClaimStatus.Granted;
        var showInsertCoin = !hasActiveTime || claimInFlight;
        var showAddTime = hasActiveTime && !claimInFlight;

        ClaimText.Text = state.Status switch
        {
            ClaimStatus.Idle => "Claim: (idle)",
            ClaimStatus.Pending => "Claim: requesting coin slot",
            ClaimStatus.Granted => $"Claim: drop coins now ({state.GrantedRemainingSec}s left)",
            ClaimStatus.Denied => $"Claim: {BuildCustomerDenyText(state)}",
            ClaimStatus.TimedOut => "Claim: no coin detected",
            _ => "Claim: unavailable"
        };

        // Push claim state down to the overlay if visible — drives button enabled/content + status line.
        _overlay?.UpdateClaimState(state);
        UpdateLockOverlayShutdownCountdown(sessionState, state, connectionState);

        InsertCoinMainButton.Visibility = showInsertCoin
            ? Visibility.Visible
            : Visibility.Collapsed;
        AddTimeMainButton.Visibility = showAddTime
            ? Visibility.Visible
            : Visibility.Collapsed;
        DoneMainButton.Visibility = claimInFlight
            ? Visibility.Visible
            : Visibility.Collapsed;

        InsertCoinMainButton.IsEnabled = state.Status switch
        {
            ClaimStatus.Idle => connectionState.CanRequestCoinSlot && !hasActiveTime,
            ClaimStatus.Denied => connectionState.CanRequestCoinSlot && !hasActiveTime
                && state.DenyReason != "station_unknown" && state.DenyReason != "abuse_ban",
            ClaimStatus.TimedOut => connectionState.CanRequestCoinSlot && !hasActiveTime,
            _ => false
        };

        InsertCoinMainButton.Content = !connectionState.CanRequestCoinSlot
            ? "Offline"
            : (state.Status switch
            {
                ClaimStatus.Pending => "Requesting...",
                ClaimStatus.Granted => $"Drop Coins ({state.GrantedRemainingSec}s)",
                _ => "Insert Coin"
            });

        AddTimeMainButton.IsEnabled = canStartClaim && hasActiveTime;
        AddTimeMainButton.Content = !connectionState.CanRequestCoinSlot
            ? "Offline"
            : (state.Status switch
            {
                ClaimStatus.Pending => "Requesting...",
                ClaimStatus.Granted => $"Drop Coins ({state.GrantedRemainingSec}s)",
                _ => "Add Time"
            });

        DoneMainButton.IsEnabled = state.CanShowDoneButton && connectionState.CanRequestCoinSlot;
        UpdateActiveOverlay(_sessionTracker.GetCurrentState(), state);
        RefreshAllUpdateBadges();
    }

    private void UpdateLockOverlayShutdownCountdown(
        SessionTrackerState sessionState,
        ClaimTrackerState claimState,
        ClientConnectionState connectionState)
    {
        if (_overlay is null)
        {
            _idleShutdownCountdown.Reset();
            return;
        }

        var deviceType = !string.IsNullOrWhiteSpace(sessionState.DeviceType)
            ? sessionState.DeviceType
            : _config?.DeviceType ?? "pc";
        var remainingSec = _idleShutdownCountdown.Update(
            new PcIdleShutdownCountdownInput(
                PolicyEnabled: _pcPolicyConfig.IdleShutdown.Enabled,
                IdleMinutes: _pcPolicyConfig.IdleShutdown.IdleMinutes,
                DeviceType: deviceType,
                ConnectionMode: connectionState.Mode,
                HasSession: sessionState.HasSession,
                Locked: sessionState.Locked,
                RemainingSec: sessionState.EffectiveRemainingSec,
                ClaimStatus: claimState.Status));
        _overlay.UpdateAutoShutdownCountdown(remainingSec);
    }

    private void ShowActiveOverlay(SessionTrackerState sessionState, ClaimTrackerState claimState)
    {
        if (_activeOverlay is null)
        {
            _activeOverlay = new ActiveSessionOverlayWindow();
            _activeOverlay.AddTimeClicked += OnInsertCoinClicked;
            _activeOverlay.DoneClicked += OnDoneClicked;
            _activeOverlay.ClearTimeClicked += OnClearTimeClicked;
            _activeOverlay.MinimizeToTrayClicked += (_, _) => HideActiveOverlayToTray();
            if (_updateHandler is not null)
                _activeOverlay.WireUpdateHandler(_updateHandler, _shutdown);
        }

        UpdateActiveOverlay(sessionState, claimState);
        if (!_activeOverlayHiddenToTray && !_activeOverlay.IsVisible)
        {
            _activeOverlay.Show();
        }
    }

    private void UpdateActiveOverlay(SessionTrackerState sessionState, ClaimTrackerState claimState)
    {
        if (_activeOverlay is null ||
            !sessionState.HasSession ||
            sessionState.Locked ||
            sessionState.EffectiveRemainingSec <= 0)
        {
            return;
        }

        var connectionState = GetConnectionState(sessionState);
        _activeOverlay.UpdateConnectionState(connectionState);
        _activeOverlay.UpdateState(
            sessionState,
            claimState,
            connectionState.CanRequestCoinSlot,
            _config?.CountdownWarning ?? new CountdownWarningConfig());
    }

    private bool ShouldShowTimeEndingWarning(SessionTrackerState state)
    {
        var warning = _config?.CountdownWarning;
        return warning?.Enabled == true &&
               state.HasSession &&
               !state.Locked &&
               state.EffectiveRemainingSec > 0 &&
               state.EffectiveRemainingSec <= warning.FirstWarningSec;
    }

    private void HideActiveOverlayToTray()
    {
        _activeOverlayHiddenToTray = true;
        _activeOverlay?.Hide();
        Hide();
    }

    private void RestoreFromTray()
    {
        _activeOverlayHiddenToTray = false;
        Show();
        Activate();

        var sessionState = _sessionTracker.GetCurrentState();
        if (sessionState.HasSession && !sessionState.Locked && sessionState.EffectiveRemainingSec > 0)
        {
            ShowActiveOverlay(sessionState, _claimTracker.GetCurrentState());
            _activeOverlay?.Activate();
        }
    }

    private void HideActiveOverlay(bool clearTrayHidden)
    {
        if (clearTrayHidden)
        {
            _activeOverlayHiddenToTray = false;
        }

        if (_activeOverlay is not null)
        {
            _activeOverlay.Hide();
        }
    }

    private void ShowOverlay(SessionTrackerState state)
    {
        if (_config?.Debug.DisableLockOverlay == true || _adminLockBypass)
        {
            HideOverlay();
            return;
        }

        if (_overlay is null)
        {
            _overlay = new LockOverlayWindow();
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.InsertCoinClicked += OnInsertCoinClicked;
            _overlay.DoneClicked += OnDoneClicked;
            _overlay.AdminUnlocked += OnAdminUnlocked;
            _overlay.UpdateAdminConfig(_adminConfig, _allowFallbackAdminPassword);
            if (_updateHandler is not null)
                _overlay.WireUpdateHandler(_updateHandler, _shutdown);
        }

        _overlay.UpdateState(state.StationId, state.EffectiveRemainingSec);
        _overlay.UpdateRateText(BuildLockedRateText(state));
        _overlay.UpdateLockedMinimumPesos(GetLockedMinimumPesos(state));
        _overlay.UpdateConnectionState(GetConnectionState(state));
        _overlay.UpdateClaimState(_claimTracker.GetCurrentState());

        if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }
        LockOverlayEnforcer.BringToFront(_overlay);
    }

    private void RunLockedTransitionActions(SessionTrackerState state)
    {
        if (!state.HasSession ||
            !state.Locked ||
            string.Equals(_lockActionsSessionId, state.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _lockActionsSessionId = state.SessionId;
        if (_pcPolicyConfig.AutoCloseOnLock.Enabled &&
            string.Equals(_config?.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => _appCloser.CloseUserAppsAsync(_shutdown.Token));
        }
    }

    private SessionTrackerState BuildStartupLockedState()
    {
        return new SessionTrackerState(
            HasSession: true,
            SessionId: "startup-lock",
            StationId: _config?.StationId ?? string.Empty,
            DeviceType: _config?.DeviceType ?? "pc",
            Locked: true,
            EffectiveRemainingSec: 0,
            LastCreditSeqNo: 0,
            ServerUpdatedAt: string.Empty);
    }

    private void OnAdminUnlocked(object? sender, EventArgs e)
    {
        _adminLockBypass = true;
        HideOverlay();

        if (_adminWindow is null)
        {
            _adminWindow = new AdminModeWindow();
            _adminWindow.Closed += (_, _) => _adminWindow = null;
            _adminWindow.ReturnToLockClicked += OnReturnToLockClicked;
        }

        if (_updateHandler is not null)
            _adminWindow.WireUpdateHandler(_updateHandler, _shutdown);

        _adminWindow.RefreshUpdateBadge(_claimTracker.GetCurrentState().Status);

        if (!_adminWindow.IsVisible)
            _adminWindow.Show();
        _adminWindow.Activate();
    }

    private void OnReturnToLockClicked(object? sender, EventArgs e)
    {
        _adminLockBypass = false;
        _adminWindow?.Hide();
        var state = _sessionTracker.GetCurrentState();
        if (!state.HasSession)
        {
            ShowOverlay(BuildStartupLockedState());
        }
        else if (state.Locked)
        {
            ShowOverlay(state);
        }
    }

    private void HideOverlay()
    {
        _idleShutdownCountdown.Reset();
        _overlay?.UpdateAutoShutdownCountdown(null);
        if (_overlay is not null && _overlay.IsVisible)
        {
            _overlay.Hide();
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Slotify Client",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && System.IO.File.Exists(exePath))
            {
                return Drawing.Icon.ExtractAssociatedIcon(exePath) ?? Drawing.SystemIcons.Application;
            }
        }
        catch
        {
            // Best-effort cosmetic branding. Never break the rental client
            // because Windows refused to extract the embedded icon.
        }

        return Drawing.SystemIcons.Application;
    }

    private string BuildLockedRateText(SessionTrackerState state)
    {
        var deviceType = !string.IsNullOrWhiteSpace(state.DeviceType)
            ? state.DeviceType
            : _config?.DeviceType ?? "pc";

        var rule = TryGetInsertCoinRule(deviceType)
                   ?? new InsertCoinRateRuleMessage
                   {
                       StarterMaxPesos = 7,
                       StarterSeconds = 1800,
                       NormalizedFromPesos = 10,
                       PerPesoSeconds = 360
                   };

        var starter =
            $"\u20b1{rule.StarterMaxPesos} = {FormatRateDuration(CalculateInsertCoinSeconds(rule, rule.StarterMaxPesos))} start";
        var normalized =
            $"\u20b1{rule.NormalizedFromPesos} = {FormatRateDuration(CalculateInsertCoinSeconds(rule, rule.NormalizedFromPesos))}";
        var twenty = $"\u20b120 = {FormatRateDuration(CalculateInsertCoinSeconds(rule, 20))}";

        return $"{starter}, then {normalized} | {twenty}";
    }

    private static string BuildCustomerDenyText(ClaimTrackerState state) =>
        state.DenyReason switch
        {
            "slot_in_use" => "slot in use, please wait",
            "no_acceptor_online" => "coin acceptor offline",
            "station_unknown" => "station not set up",
            _ => "coin slot unavailable"
        };

    private InsertCoinRateRuleMessage? TryGetInsertCoinRule(string deviceType)
    {
        if (_rateConfig?.InsertCoinRules is null)
        {
            return null;
        }

        if (_rateConfig.InsertCoinRules.TryGetValue(deviceType, out var rule))
        {
            return rule;
        }

        return _rateConfig.InsertCoinRules.TryGetValue("pc", out var pcRule)
            ? pcRule
            : null;
    }

    private static int CalculateInsertCoinSeconds(InsertCoinRateRuleMessage rule, int totalPesos)
    {
        if (totalPesos <= 0)
        {
            return 0;
        }

        if (totalPesos < rule.StarterMaxPesos)
        {
            return 0;
        }

        if (totalPesos == rule.StarterMaxPesos)
        {
            return rule.StarterSeconds;
        }

        if (totalPesos < rule.NormalizedFromPesos)
        {
            return rule.StarterSeconds + ((totalPesos - rule.StarterMaxPesos) * rule.PerPesoSeconds);
        }

        return totalPesos * rule.PerPesoSeconds;
    }

    private static string FormatRateDuration(int totalSeconds)
    {
        var totalMinutes = totalSeconds / 60;
        if (totalMinutes < 60)
        {
            return $"{totalMinutes} min";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        var hourLabel = hours == 1 ? "hr" : "hrs";

        return minutes == 0
            ? $"{hours} {hourLabel}"
            : $"{hours} {hourLabel} {minutes} min";
    }

    private static string FormatDuration(long totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;

        _toastHideTimer?.Stop();
        _toastHideTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(3),
            DispatcherPriority.Normal,
            (_, _) => HideToast(),
            Dispatcher);
        _toastHideTimer.Start();
    }

    private void HideToast()
    {
        _toastHideTimer?.Stop();
        _toastHideTimer = null;
        ToastBorder.Visibility = Visibility.Collapsed;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        HideActiveOverlayToTray();
    }
}
