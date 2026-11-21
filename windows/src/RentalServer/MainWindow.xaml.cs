using MQTTnet;
using RentalServer.Core.Admin;
using RentalServer.Core.Acceptors;
using RentalServer.Core.Abuse;
using RentalServer.Core.Backups;
using RentalServer.Core.Claims;
using RentalServer.Core.Credits;
using RentalServer.Core.EventLog;
using RentalServer.Core.Mqtt;
using RentalServer.Core.Persistence;
using RentalServer.Core.PcPolicy;
using RentalServer.Core.Rates;
using RentalServer.Core.Sessions;
using RentalServer.Core.Stations;
using RentalServer.Core.TabletApps;
using RentalServer.Core.TabletAdmin;
using RentalServer.Core.Time;
using RentalShared.Mqtt;
using RentalShared.Updates;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RentalServer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<StationGridRow> _stations = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _databasePath;

    private readonly StationRepository _stationRepository;
    private readonly AcceptorRepository _acceptorRepository;
    private readonly ClaimRepository _claimRepository;
    private readonly RateRepository _rateRepository;
    private readonly TabletAppRepository _tabletAppRepository;
    private readonly TabletAdminRepository _tabletAdminRepository;
    private readonly PcPolicyRepository _pcPolicyRepository;
    private readonly AbusePreventionRepository _abusePreventionRepository;
    private readonly SessionRepository _sessionRepository;
    private readonly EventLogRepository _eventLogRepository;
    private readonly PendingCreditsRepository _pendingCreditsRepository;

    private readonly StationRegistry _stationRegistry;
    private readonly AcceptorRegistry _acceptorRegistry;
    private readonly TabletAppInventoryRegistry _tabletAppInventoryRegistry;

    private readonly RentalServerMqttService _mqtt;
    private readonly SessionService _sessionService;
    private readonly AbusePreventionService _abusePrevention;
    private readonly ClaimService _claimService;
    private readonly PulseService _pulseService;
    private readonly AdminTimeService _adminTimeService;
    private readonly AdminCommandService _adminCommandService;
    // requestId -> operator-facing label ("Restart Client" etc.) for the most
    // recent admin commands, so an inbound ack can name what it is confirming.
    // Touched only on the UI thread (dispatch continuation + Dispatcher.Invoke
    // in the ack handler), so no lock is needed.
    private readonly Dictionary<string, string> _pendingCommandLabels = new();
    private readonly RateService _rateService;
    private readonly TabletAppConfigService _tabletAppConfigService;
    private readonly TabletAdminConfigService _tabletAdminConfigService;
    private readonly PcPolicyConfigService _pcPolicyConfigService;
    private readonly PcIdleShutdownTracker _pcIdleShutdownTracker;
    private readonly ServerBackupService _backupService = new();
    private readonly ServerBackupOptions _backupOptions;
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    private readonly ServerMqttConfig _mqttConfig;
    private UdpDiscoveryBroadcaster? _udpBroadcaster;
    private MdnsDiscoveryAdvertiser? _mdnsAdvertiser;

    // S46 follow-up: candidate-adapter list + chosen advertise IP, surfaced via
    // the existing VerboseStatusIcon tooltip (see ApplyStatus) so an operator on
    // a multi-homed server can see which NIC got picked without a Debug build.
    private string? _discoveryDiagnosticsText;

    private HttpFileServer? _httpServer;
    private OnlineUpdatePoller? _onlineUpdatePoller;

    private readonly PeriodicTimer _tickTimer = new(TimeSpan.FromSeconds(1));
    private readonly PeriodicTimer _backupTimer = new(TimeSpan.FromHours(1));
    private Task? _tickLoop;
    private Task? _backupLoop;
    private IReadOnlyList<AcceptorRecord> _acceptors = Array.Empty<AcceptorRecord>();
    private string _acceptorGridStateText = "NO ACCEPTOR";
    private string? _activeLedClaimId;
    private string? _activeLedStationId;
    private string? _activeLedAcceptorId;
    private bool _isAddingTime;
    private bool _isEditingRates;
    private bool _isEditingTabletApps;
    private bool _isEditingTabletAdmin;
    private bool _isEditingPcPolicy;
    private bool _isReleasingAcceptor;
    private bool _isBackingUp;
    private PcPolicyConfig _pcPolicyConfig = PcPolicyConfig.Default;
    private Point? _stationDragStartPoint;
    private StationGridRow? _stationDragSource;

    public MainWindow()
    {
        InitializeComponent();

        _databasePath = RentalDatabase.GetDefaultDatabasePath();
        _backupOptions = CreateBackupOptions(_databasePath, reason: "scheduled");
        _stationRepository = new StationRepository(_databasePath);
        _acceptorRepository = new AcceptorRepository(_databasePath);
        _claimRepository = new ClaimRepository(_databasePath);
        _rateRepository = new RateRepository(_databasePath);
        _tabletAppRepository = new TabletAppRepository(_databasePath);
        _tabletAdminRepository = new TabletAdminRepository(_databasePath);
        _pcPolicyRepository = new PcPolicyRepository(_databasePath);
        _abusePreventionRepository = new AbusePreventionRepository(_databasePath);
        _sessionRepository = new SessionRepository(_databasePath);
        _eventLogRepository = new EventLogRepository(_databasePath);
        _pendingCreditsRepository = new PendingCreditsRepository(_databasePath);

        _stationRegistry = new StationRegistry(_stationRepository);
        _acceptorRegistry = new AcceptorRegistry(_acceptorRepository);
        _tabletAppInventoryRegistry = new TabletAppInventoryRegistry();

        _mqttConfig = LoadServerMqttConfig();
        _mqtt = new RentalServerMqttService(_mqttConfig);
        _sessionService = new SessionService(
            _sessionRepository,
            _mqtt,
            _mqtt,
            new SystemMonotonicClock(),
            _eventLogRepository,
            // S26: optional pending_credits collaborator. SessionService inserts a
            // row on each credit publish; the credit/ack handler below deletes it
            // on receipt; the 1 Hz TickAsync sweep republishes anything that has
            // been outstanding longer than the protocol's 5 s ACK window.
            _pendingCreditsRepository);
        _adminTimeService = new AdminTimeService(_stationRepository, _sessionService, _eventLogRepository);
        _adminCommandService = new AdminCommandService(_stationRepository, _mqtt, _eventLogRepository);
        // S21a: RateService owns the admin-edit + retained-publish workflow.
        _rateService = new RateService(_rateRepository, _mqtt, _eventLogRepository);
        _tabletAppConfigService = new TabletAppConfigService(
            _tabletAppRepository,
            _mqtt,
            _eventLogRepository);
        _tabletAdminConfigService = new TabletAdminConfigService(
            _tabletAdminRepository,
            _mqtt,
            _eventLogRepository);
        _pcPolicyConfigService = new PcPolicyConfigService(
            _pcPolicyRepository,
            _mqtt,
            _eventLogRepository);
        _pcIdleShutdownTracker = new PcIdleShutdownTracker(new SystemMonotonicClock());

        _abusePrevention = new AbusePreventionService();

        _claimService = new ClaimService(
            _claimRepository,
            _acceptorRepository,
            _mqtt,
            _mqtt,
            resolveRateModeAsync: (stationId, _) =>
                Task.FromResult(_sessionService.GetLiveRemainingSeconds(stationId) is > 0
                    ? ClaimRateMode.AddTime
                    : ClaimRateMode.InsertCoin),
            applyResolvedCreditAsync: async (claim, seconds, ct) =>
            {
                var station = await _stationRepository.GetAsync(claim.StationId, ct);
                if (station is null)
                {
                    throw new InvalidOperationException($"Station '{claim.StationId}' is not registered.");
                }

                await _sessionService.StartSessionAsync(claim.StationId, station.DeviceType, ct);
                await _sessionService.ApplyDeferredCreditAsync(
                    claim.StationId,
                    seconds,
                    cancellationToken: ct);
            },
            eventLogger: _eventLogRepository,
            abusePrevention: _abusePrevention);

        _pulseService = new PulseService(
            _acceptorRepository,
            _claimRepository,
            _stationRepository,
            _rateRepository,
            _sessionService,
            _claimService,
            _mqtt,
            _eventLogRepository);

        // Inbound routing — host wires the MQTT service's delegate properties to the
        // right handlers. Each delegate is invoked at most once per inbound message.
        _mqtt.OnStationStatus = HandleStationStatusAsync;
        _mqtt.OnAcceptorStatus = (topic, payload, ct) =>
            _acceptorRegistry.HandleStatusMessageAsync(topic, payload, ct);
        _mqtt.OnClaimRequest = (topic, payload, ct) =>
            _claimService.HandleClaimRequestAsync(topic, payload, ct);
        _mqtt.OnClaimEnd = (topic, payload, ct) =>
            _claimService.HandleClaimEndAsync(topic, payload, ct);
        _mqtt.OnSessionClear = HandleSessionClearAsync;
        _mqtt.OnPulse = (topic, payload, ct) =>
            _pulseService.HandlePulseAsync(topic, payload, ct);
        _mqtt.OnCreditAck = HandleCreditAckAsync;
        _mqtt.OnCommandAck = HandleCommandAckAsync;
        _mqtt.OnStationAppInventory = (topic, payload, ct) =>
            _tabletAppInventoryRegistry.HandleInventoryMessageAsync(topic, payload, ct);

        StationGrid.ItemsSource = _stations;
        DatabasePathText.Text = $"Database: {_databasePath}";
        BackupStatusText.Text = $"Backup: folder {RentalDatabase.GetDefaultBackupRootPath()}";

        _stationRegistry.StationsChanged += OnStationsChanged;
        _acceptorRegistry.AcceptorsChanged += OnAcceptorsChanged;
        _mqtt.StatusChanged += OnMqttStatusChanged;
        _sessionService.SessionChanged += OnSessionChanged;
        _claimService.ClaimGranted += OnClaimGranted;
        _claimService.ClaimResolved += OnClaimResolved;
    }

    private async Task HandleStationStatusAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken)
    {
        await _stationRegistry.HandleStatusMessageAsync(topic, payload, cancellationToken);

        // Fail-safe lock: when a station comes online with no session yet, create
        // a locked (0-time) session so a freshly-imaged PC is locked by default
        // rather than usable for free. StartSessionAsync adopts any existing active
        // session (in memory or DB), so a station that already has time is untouched.
        try
        {
            if (!MqttTopics.TryGetStationIdFromStatusTopic(topic, out var stationId))
            {
                return;
            }

            var status = JsonSerializer.Deserialize<StationStatusMessage>(
                payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (status is { Online: true } && status.DeviceType is "pc" or "phn" or "tab")
            {
                await _sessionService.StartSessionAsync(stationId, status.DeviceType!, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[autolock] ensure-locked-session failed: {ex.Message}");
        }
    }

    private async Task HandleSessionClearAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken)
    {
        if (!MqttTopics.TryGetStationIdFromSessionClearTopic(topic, out var stationId))
        {
            return;
        }

        var message = JsonSerializer.Deserialize<SessionClearRequestMessage>(
            payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (message is null ||
            !string.Equals(message.StationId, stationId, StringComparison.Ordinal))
        {
            return;
        }

        var station = await _stationRepository.GetAsync(stationId, cancellationToken);
        if (station is null)
        {
            return;
        }

        await _sessionService.ClearSessionAsync(
            stationId,
            station.DeviceType,
            cancellationToken);
    }

    private async Task HandleCreditAckAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken)
    {
        if (!MqttTopics.TryGetStationIdFromCreditAckTopic(topic, out var stationId))
        {
            return;
        }

        CreditAckMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<CreditAckMessage>(
                payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // Malformed ACK from a buggy station — drop silently; the retry sweep
            // will keep republishing the pending row until a parseable ACK arrives.
            return;
        }

        if (message is null ||
            !string.Equals(message.StationId, stationId, StringComparison.Ordinal))
        {
            return;
        }

        // DeleteAsync returns 0 when the seqno was already cleared (duplicate
        // ACK after a successful first delivery) — that's expected per protocol
        // §7 ("Station ACKs even on duplicate") and is not an error.
        await _pendingCreditsRepository.DeleteAsync(stationId, message.SeqNo, cancellationToken);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = "v" + GetRunningVersionDisplay();
        try
        {
            await _stationRegistry.InitializeAsync(_shutdown.Token);
            await _acceptorRegistry.InitializeAsync(_shutdown.Token);
            await _sessionService.InitializeAsync(_shutdown.Token);
            await _claimService.InitializeAsync(_shutdown.Token);
            await _rateRepository.InitializeAsync(_shutdown.Token);
            await _rateRepository.EnsureSeededAsync(
                FormatUtc(DateTimeOffset.UtcNow),
                _shutdown.Token);
            await _tabletAppRepository.InitializeAsync(_shutdown.Token);
            await _tabletAppRepository.EnsureSeededAsync(
                FormatUtc(DateTimeOffset.UtcNow),
                _shutdown.Token);
            await _tabletAdminRepository.InitializeAsync(_shutdown.Token);
            await _tabletAdminRepository.EnsureSeededAsync(
                FormatUtc(DateTimeOffset.UtcNow),
                _shutdown.Token);
            await _pcPolicyRepository.InitializeAsync(_shutdown.Token);
            await _pcPolicyRepository.EnsureSeededAsync(
                FormatUtc(DateTimeOffset.UtcNow),
                _shutdown.Token);
            _pcPolicyConfig = await _pcPolicyRepository.GetCurrentConfigAsync(_shutdown.Token)
                              ?? PcPolicyConfig.Default;
            await _abusePreventionRepository.InitializeAsync(_shutdown.Token);
            await _abusePreventionRepository.EnsureSeededAsync(
                FormatUtc(DateTimeOffset.UtcNow),
                _shutdown.Token);
            _abusePrevention.UpdateConfig(
                await _abusePreventionRepository.GetCurrentConfigAsync(_shutdown.Token)
                ?? AbusePreventionService.DefaultConfig);
            // S22: event_log must exist before any service tries to append. Indexed
            // by ts, event_type, and station_id for filterable reports.
            await _eventLogRepository.InitializeAsync(_shutdown.Token);
            // S26: pending_credits queue must exist before SessionService starts
            // publishing — InsertAsync on a missing table would fail and abort
            // the credit pipeline. FK on station_id means the table depends on
            // stations existing first, which is fine because StationRegistry
            // initialized above creates the stations table.
            await _pendingCreditsRepository.InitializeAsync(_shutdown.Token);

            // Preload SessionService's in-memory cache from the SQLite sessions
            // table so the new Time Remaining column on the station grid shows
            // real values immediately after server start, instead of "—" until
            // each station happens to be touched by a claim/admin action.
            var allStations = await _stationRepository.GetAllAsync(_shutdown.Token);
            var deviceTypeByStation = allStations.ToDictionary(
                s => s.StationId,
                s => s.DeviceType,
                StringComparer.Ordinal);
            await _sessionService.PreloadActiveSessionsAsync(deviceTypeByStation, _shutdown.Token);
        }
        catch (Exception ex)
        {
            ApplyStatus(new MqttConnectionStatus(false, "Startup initialization failed.", ex.Message));
            return;
        }

        _tickLoop = Task.Run(() => TickLoopAsync(_shutdown.Token));
        _backupLoop = Task.Run(() => BackupLoopAsync(_shutdown.Token));
        await ConnectAsync();

        // Publish retained config after the broker is up so fresh clients see
        // rates and tablet app whitelist immediately on subscribe.
        try
        {
            await _rateService.PublishCurrentAsync(_shutdown.Token);
            await _tabletAppConfigService.PublishCurrentAsync(_shutdown.Token);
            await _tabletAdminConfigService.PublishCurrentAsync(_shutdown.Token);
            await _pcPolicyConfigService.PublishCurrentAsync(_shutdown.Token);
            await PublishAbusePreventionConfigAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ApplyStatus(new MqttConnectionStatus(_mqtt.IsConnected, "Initial config publish failed.", ex.Message));
        }

        // S46: LAN discovery — broadcast broker endpoint so clients with
        // broker.host="auto" can find the server without a hardcoded IP.
        StartDiscoveryBroadcaster();

        // S50a: start the embedded HTTP file server so clients can download
        // updated EXEs/APKs. Serves from %ProgramData%\RentalSystem\updates\.
        // On Windows, HttpListener requires a URL ACL reservation for the LAN
        // IP. If not registered, the server falls back gracefully and updates
        // can still be downloaded through the broker (MQTT is not affected).
        var updatesDir = Path.Combine(
            Path.GetDirectoryName(_databasePath) ?? AppContext.BaseDirectory,
            "updates");
        try
        {
            _httpServer = new HttpFileServer(8081, updatesDir);
            _httpServer.Start();
            Debug.WriteLine($"[update] HTTP file server started at {_httpServer.BaseUrl}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[update] HTTP file server failed to start: {ex.Message}");
            Debug.WriteLine($"[update] Run: netsh http add urlacl url=http://{GetLanIpAddress()}:8081/ user=Everyone");
        }

        // Online updates: bridge GitHub update.json -> local broker, so a release
        // can be published from anywhere (scripts/publish-online.ps1 + gh) without
        // touching the shop's IP. The broker is local to the server, so an
        // "auto"/blank configured host means 127.0.0.1.
        try
        {
            var updateBrokerHost = string.IsNullOrWhiteSpace(_mqttConfig.Host) ||
                                   _mqttConfig.Host.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "127.0.0.1"
                : _mqttConfig.Host;
            _onlineUpdatePoller = new OnlineUpdatePoller(updateBrokerHost, _mqttConfig.Port,
                m => Debug.WriteLine(m));
            _onlineUpdatePoller.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[online-update] poller failed to start: {ex.Message}");
        }
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    // S22: opens the audit-log explorer. Read-only against the shared
    // EventLogRepository so a long-running query never blocks the main
    // window's tick loop.
    private void OnTransactionsClicked(object sender, RoutedEventArgs e)
    {
        var window = new TransactionsWindow(_eventLogRepository) { Owner = this };
        window.Show();
    }

    // S33: opens the simplified daily coin-history panel. Plain-language
    // columns (no JSON, no internal event_type) for the operator's daily
    // ops view. Defaults to today; date picker for older days. Sister
    // window to TransactionsWindow which keeps the full forensic view.
    private void OnCoinHistoryClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new CoinHistoryWindow(_eventLogRepository) { Owner = this };
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to open Coin History: {ex}",
                "Coin History",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnHealthClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new HealthWindow(
                _acceptorRepository,
                _stationRepository,
                _sessionService,
                _claimService,
                _pendingCreditsRepository,
                _eventLogRepository,
                _mqtt,
                _databasePath) { Owner = this };
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open Health: {ex.Message}",
                "Health", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnOpenStationClicked(object sender, RoutedEventArgs e)
    {
        if (StationGrid.SelectedItem is not StationGridRow selected)
        {
            return;
        }

        try
        {
            // Tablet: send MQTT admin-unlock command. The tablet app
            // subscribes to rental/station/{id}/admin/open and enters
            // maintenance mode on receipt (stops Lock Task, shows bars).
            if (selected.DeviceType == "tab")
            {
                if (!_mqtt.IsConnected)
                {
                    MessageBox.Show(this,
                        "MQTT broker not connected. Cannot send admin command to tablet.",
                        "Open as Admin", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(MqttTopics.StationAdminOpen(selected.StationId))
                    .WithPayload("{}")
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqtt.PublishRawAsync(message);
                SetAddTimeStatus($"Sent admin-open command to {selected.StationId}.", isError: false);
                return;
            }

            // PC: launch the client with --maintenance flag
            var clientPath = Path.Combine(
                AppContext.BaseDirectory.Replace("RentalServer", "RentalClient.Windows"),
                "RentalClient.Windows.exe");

            if (!File.Exists(clientPath))
            {
                MessageBox.Show(this,
                    $"RentalClient not found at:\n{clientPath}",
                    "Open as Admin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = clientPath,
                Arguments = "--maintenance",
                WorkingDirectory = Path.GetDirectoryName(clientPath),
                WindowStyle = ProcessWindowStyle.Normal
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open {selected.StationId}: {ex.Message}",
                "Open as Admin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // S21a: open the rate editor modal, pre-populated with the current sheet.
    // On Save, RateService persists a new version + publishes the retained sheet.
    private async void OnEditRatesClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isEditingRates)
        {
            return;
        }

        _isEditingRates = true;
        try
        {
            RateSheet? current;
            try
            {
                current = await _rateService.GetCurrentRateSheetAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not load current rates: {ex.Message}",
                    "Edit Rates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (current is null)
            {
                MessageBox.Show(
                    this,
                    "No rate sheet seeded yet. Restart the server so defaults are written.",
                    "Edit Rates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dialog = new RateEditorWindow(current) { Owner = this };
            var saved = dialog.ShowDialog() == true;
            if (!saved || dialog.EditedRateSheet is null)
            {
                return;
            }

            try
            {
                var newVersion = await _rateService.UpdateRateSheetAsync(dialog.EditedRateSheet, _shutdown.Token);
                MessageBox.Show(
                    this,
                    $"Rate version {newVersion} published.",
                    "Edit Rates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Failed to publish new rates: {ex.Message}",
                    "Edit Rates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _isEditingRates = false;
        }
    }

    private async void OnTabletAppsClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isEditingTabletApps)
        {
            return;
        }

        _isEditingTabletApps = true;
        try
        {
            TabletAppConfig? current;
            try
            {
                current = await _tabletAppConfigService.GetCurrentConfigAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Could not load tablet apps: {ex.Message}",
                    "Tablet Apps",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (current is null)
            {
                MessageBox.Show(
                    this,
                    "No tablet app config seeded yet. Restart the server so defaults are written.",
                    "Tablet Apps",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dialog = new TabletAppEditorWindow(current, _tabletAppInventoryRegistry.GetAll()) { Owner = this };
            var saved = dialog.ShowDialog() == true;
            if (!saved || dialog.EditedConfig is null)
            {
                return;
            }

            try
            {
                var version = await _tabletAppConfigService.UpdateConfigAsync(dialog.EditedConfig, _shutdown.Token);
                MessageBox.Show(
                    this,
                    $"Tablet app config version {version} published.",
                    "Tablet Apps",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Failed to publish tablet apps: {ex.Message}",
                    "Tablet Apps",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _isEditingTabletApps = false;
        }
    }

    private async void OnTabletAdminClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isEditingTabletAdmin)
        {
            return;
        }

        _isEditingTabletAdmin = true;
        try
        {
            var dialog = new TabletAdminPasswordWindow { Owner = this };
            var saved = dialog.ShowDialog() == true;
            if (!saved || string.IsNullOrWhiteSpace(dialog.Password))
            {
                return;
            }

            try
            {
                var version = await _tabletAdminConfigService.UpdatePasswordAsync(
                    dialog.Password,
                    _shutdown.Token);
                MessageBox.Show(
                    this,
                    $"Tablet admin password version {version} published.",
                    "Tablet Admin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Failed to publish tablet admin password: {ex.Message}",
                    "Tablet Admin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _isEditingTabletAdmin = false;
        }
    }

    private async void OnPcPolicyClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isEditingPcPolicy)
        {
            return;
        }

        _isEditingPcPolicy = true;
        try
        {
            var currentPcPolicy = await _pcPolicyRepository.GetCurrentConfigAsync(_shutdown.Token)
                                  ?? PcPolicyConfig.Default;
            var currentAbusePolicy = await _abusePreventionRepository.GetCurrentConfigAsync(_shutdown.Token)
                                     ?? _abusePrevention.CurrentConfig;
            var dialog = new PcPolicyWindow(currentPcPolicy, currentAbusePolicy) { Owner = this };
            var saved = dialog.ShowDialog() == true;
            if (!saved ||
                dialog.EditedPcPolicyConfig is null ||
                dialog.EditedAbuseConfig is null)
            {
                return;
            }

            try
            {
                var (version, config) = await _pcPolicyConfigService.UpdateConfigAsync(
                    dialog.EditedPcPolicyConfig,
                    _shutdown.Token);
                var abusePublishedAt = FormatUtc(DateTimeOffset.UtcNow);
                var abuseVersion = await _abusePreventionRepository.PublishNewVersionAsync(
                    dialog.EditedAbuseConfig,
                    abusePublishedAt,
                    _shutdown.Token);

                _pcPolicyConfig = config;
                _abusePrevention.UpdateConfig(dialog.EditedAbuseConfig);
                await PublishAbusePreventionConfigAsync(_shutdown.Token);

                SetAddTimeStatus(
                    $"PC & Coin Slot Policy saved. PC v{version}; Abuse v{abuseVersion}. " +
                    $"Idle shutdown: {(config.IdleShutdownEnabled ? $"{config.IdleShutdownMinutes} min" : "off")}. " +
                    $"Abuse: {(dialog.EditedAbuseConfig.Enabled ? $"{dialog.EditedAbuseConfig.MaxEmptyClaims} empty / {dialog.EditedAbuseConfig.BanMinutes} min ban" : "off")}.",
                    isError: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Failed to publish policy: {ex.Message}",
                    "PC & Coin Slot Policy",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _isEditingPcPolicy = false;
        }
    }

    private async void OnAddTimeClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        var targets = SelectedStationsOrFail("Add Time");
        if (targets.Length == 0)
        {
            return;
        }

        if (!TryGetAddTimeSeconds(out var secondsToAdd, out var validationError))
        {
            SetAddTimeStatus(validationError, isError: true);
            return;
        }

        await ApplyAddTimeAsync(targets, secondsToAdd);
    }

    // Typed-amount Remove Time — uses the same minutes textbox as the
    // typed-amount Add Time button. Path is identical to the preset
    // RemoveTime{30,60,120} buttons except the seconds value comes from
    // the textbox instead of a hardcoded preset.
    private async void OnRemoveTimeClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        if (!TryGetAddTimeSeconds(out var secondsToRemove, out var validationError))
        {
            SetAddTimeStatus(validationError, isError: true);
            return;
        }

        await QuickRemoveTimeBatchAsync(secondsToRemove);
    }

    // S32 quick-action handlers: preset durations the operator can fire in one
    // click. Each routes through ApplyAddTimeAsync so the AdminTimeService call,
    // status text update, and control-gating behave identically to the
    // typed-minutes path.
    private async void OnAddTime30Clicked(object sender, RoutedEventArgs e) =>
        await QuickAddTimeBatchAsync(30 * 60);

    private async void OnAddTime60Clicked(object sender, RoutedEventArgs e) =>
        await QuickAddTimeBatchAsync(60 * 60);

    private async void OnAddTime120Clicked(object sender, RoutedEventArgs e) =>
        await QuickAddTimeBatchAsync(120 * 60);

    // Remove Time quick-actions mirror the Add Time presets. Each runs
    // through QuickRemoveTimeAsync → AdminTimeService.RemoveTimeAsync → the
    // session is republished retained, the audit row is emitted, the
    // status text shows what actually happened (which may differ from the
    // requested amount if the session had less time left).
    private async void OnRemoveTime30Clicked(object sender, RoutedEventArgs e) =>
        await QuickRemoveTimeBatchAsync(30 * 60);

    private async void OnRemoveTime60Clicked(object sender, RoutedEventArgs e) =>
        await QuickRemoveTimeBatchAsync(60 * 60);

    private async void OnRemoveTime120Clicked(object sender, RoutedEventArgs e) =>
        await QuickRemoveTimeBatchAsync(120 * 60);

    private async Task QuickRemoveTimeAsync(int secondsToRemove)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        var targets = SelectedStationsOrFail("Remove Time");
        if (targets.Length == 0)
        {
            return;
        }
        var selected = targets[0];

        _isAddingTime = true;
        UpdateAddTimeControls();

        try
        {
            // Warn before removing time that would lock any selected station immediately.
            var lockingTargets = targets
                .Select(row => new
                {
                    Row = row,
                    Remaining = _sessionService.GetLiveRemainingSeconds(row.StationId) ?? 0
                })
                .Where(item => item.Remaining > 0 && item.Remaining <= secondsToRemove)
                .ToArray();
            if (lockingTargets.Length > 0)
            {
                var msg = lockingTargets.Length == 1
                    ? $"Removing {FormatDuration(secondsToRemove)} will lock {lockingTargets[0].Row.StationId} " +
                      $"(currently {FormatDuration(lockingTargets[0].Remaining)} remaining). Proceed?"
                    : $"Removing {FormatDuration(secondsToRemove)} will lock {lockingTargets.Length} selected station(s):\n\n" +
                      $"{string.Join("\n", lockingTargets.Select(item => $"- {item.Row.StationId}: {FormatDuration(item.Remaining)} remaining"))}\n\nProceed?";
                var confirm = MessageBox.Show(this, msg, "Remove Time Warning",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    SetAddTimeStatus("Remove Time cancelled.", isError: false);
                    return;
                }
            }

            var result = await _adminTimeService.RemoveTimeAsync(
                selected.StationId,
                secondsToRemove,
                _shutdown.Token);

            switch (result.Status)
            {
                case AdminRemoveTimeStatus.Applied:
                    var lockedSuffix = result.NowLocked ? " (station re-locked)" : string.Empty;
                    var actualRemoved = FormatDuration(result.ActualRemovedSec);
                    var requestedDifferent = result.ActualRemovedSec != secondsToRemove;
                    var prefix = requestedDifferent
                        ? $"Removed {actualRemoved} (clamped at 0)"
                        : $"Removed {actualRemoved}";
                    var remaining = result.Session is null
                        ? 0
                        : (_sessionService.GetLiveRemainingSeconds(selected.StationId)
                           ?? result.Session.RemainingSecondsAtAnchor);
                    SetAddTimeStatus(
                        $"{prefix} from {selected.StationId}. Remaining: {FormatDuration(remaining)}.{lockedSuffix}",
                        isError: false);
                    break;

                case AdminRemoveTimeStatus.StationOffline:
                    SetAddTimeStatus($"{selected.StationId} is offline. Remove Time was not applied.", isError: true);
                    break;

                case AdminRemoveTimeStatus.StationUnknown:
                    SetAddTimeStatus($"{selected.StationId} is not registered. Remove Time was not applied.", isError: true);
                    break;

                case AdminRemoveTimeStatus.NoActiveSession:
                    SetAddTimeStatus($"{selected.StationId} has no active session — nothing to remove.", isError: true);
                    break;

                case AdminRemoveTimeStatus.NothingToRemove:
                    SetAddTimeStatus($"{selected.StationId} already at 0 — nothing to remove.", isError: true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Remove Time failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isAddingTime = false;
            UpdateAddTimeControls();
        }
    }

    private async Task QuickAddTimeAsync(int secondsToAdd)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        var selected = SelectedOnlineStationOrFail();
        if (selected is null)
        {
            return;
        }

        await ApplyAddTimeAsync(selected, secondsToAdd);
    }

    private async Task QuickAddTimeBatchAsync(int secondsToAdd)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        var targets = SelectedStationsOrFail("Add Time");
        if (targets.Length == 0)
        {
            return;
        }

        await ApplyAddTimeAsync(targets, secondsToAdd);
    }

    private async Task QuickRemoveTimeBatchAsync(int secondsToRemove)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        var targets = SelectedStationsOrFail("Remove Time");
        if (targets.Length == 0)
        {
            return;
        }

        _isAddingTime = true;
        UpdateAddTimeControls();

        try
        {
            var lockingTargets = targets
                .Select(row => new
                {
                    Row = row,
                    Remaining = _sessionService.GetLiveRemainingSeconds(row.StationId) ?? 0
                })
                .Where(item => item.Remaining > 0 && item.Remaining <= secondsToRemove)
                .ToArray();
            if (lockingTargets.Length > 0)
            {
                var msg = lockingTargets.Length == 1
                    ? $"Removing {FormatDuration(secondsToRemove)} will lock {lockingTargets[0].Row.StationId} " +
                      $"(currently {FormatDuration(lockingTargets[0].Remaining)} remaining). Proceed?"
                    : $"Removing {FormatDuration(secondsToRemove)} will lock {lockingTargets.Length} selected station(s):\n\n" +
                      $"{string.Join("\n", lockingTargets.Select(item => $"- {item.Row.StationId}: {FormatDuration(item.Remaining)} remaining"))}\n\nProceed?";
                var confirm = MessageBox.Show(this, msg, "Remove Time Warning",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    SetAddTimeStatus("Remove Time cancelled.", isError: false);
                    return;
                }
            }

            var applied = new List<string>();
            var skipped = new List<string>();
            foreach (var target in targets)
            {
                var result = await _adminTimeService.RemoveTimeAsync(
                    target.StationId,
                    secondsToRemove,
                    _shutdown.Token);

                switch (result.Status)
                {
                    case AdminRemoveTimeStatus.Applied:
                        var remaining = result.Session is null
                            ? 0
                            : (_sessionService.GetLiveRemainingSeconds(target.StationId)
                               ?? result.Session.RemainingSecondsAtAnchor);
                        var lockedSuffix = result.NowLocked ? ", locked" : string.Empty;
                        applied.Add(
                            $"{target.StationId} (-{FormatDuration(result.ActualRemovedSec)}, remaining {FormatDuration(remaining)}{lockedSuffix})");
                        break;

                    case AdminRemoveTimeStatus.StationOffline:
                        skipped.Add($"{target.StationId} (offline)");
                        break;

                    case AdminRemoveTimeStatus.StationUnknown:
                        skipped.Add($"{target.StationId} (not registered)");
                        break;

                    case AdminRemoveTimeStatus.NoActiveSession:
                        skipped.Add($"{target.StationId} (no active session)");
                        break;

                    case AdminRemoveTimeStatus.NothingToRemove:
                        skipped.Add($"{target.StationId} (already 0)");
                        break;
                }
            }

            SetAddTimeStatus(
                BuildBatchStatus("Remove Time", applied, skipped),
                isError: applied.Count == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Remove Time failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isAddingTime = false;
            UpdateAddTimeControls();
        }
    }

    /// <summary>
    /// S32 Clear Time. Destructive — pops a confirmation dialog before
    /// calling SessionService.ClearSessionAsync, which wipes remaining time
    /// to 0 and re-locks the station. Uses the same _isAddingTime gate so
    /// the buttons stay disabled during the in-flight publish.
    /// </summary>
    private async void OnClearTimeClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        await ClearTimeBatchAsync();
    }

    private async Task ClearTimeBatchAsync()
    {
        var targets = SelectedStationsOrFail("Clear Time");
        if (targets.Length == 0)
        {
            return;
        }

        var stationList = FormatStationList(targets);
        var prompt = targets.Length == 1
            ? $"Clear all remaining time for {targets[0].StationId}?\n\n" +
              "This re-locks the station immediately. The customer loses any unspent time on the current session."
            : $"Clear all remaining time for {targets.Length} selected station(s)?\n\n" +
              $"{stationList}\n\n" +
              "This re-locks each station immediately. Customers lose any unspent time on current sessions.";
        var confirm = MessageBox.Show(
            this,
            prompt,
            "Confirm Clear Time",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _isAddingTime = true;
        UpdateAddTimeControls();

        try
        {
            var applied = new List<string>();
            var skipped = new List<string>();
            foreach (var target in targets)
            {
                var session = await _sessionService.ClearSessionAsync(
                    target.StationId,
                    target.DeviceType,
                    _shutdown.Token);

                if (session is null)
                {
                    skipped.Add($"{target.StationId} (no active session)");
                }
                else
                {
                    applied.Add(target.StationId);
                }
            }

            SetAddTimeStatus(
                BuildBatchStatus("Clear Time", applied, skipped),
                isError: applied.Count == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Clear Time failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isAddingTime = false;
            UpdateAddTimeControls();
        }
    }

    private async void OnTransferTimeClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isAddingTime)
        {
            return;
        }

        if (!TryGetSelectedTransferPair(out var source, out var target, out var reason))
        {
            SetAddTimeStatus(reason, isError: true);
            return;
        }

        await ConfirmAndTransferTimeAsync(source, target);
    }

    private async void OnReleaseAcceptorClicked(object sender, RoutedEventArgs e)
    {
        if (_shutdown.IsCancellationRequested || _isReleasingAcceptor)
        {
            return;
        }

        var acceptor = _acceptors.FirstOrDefault(a => a.IsOnline);
        if (acceptor is null)
        {
            SetAddTimeStatus("Coin acceptor is offline. LED-off command was not sent.", isError: true);
            return;
        }

        _isReleasingAcceptor = true;
        UpdateAddTimeControls();

        try
        {
            var pending = await _claimService.GetPendingForAcceptorAsync(
                acceptor.AcceptorId,
                _shutdown.Token);
            var claimId = pending?.ClaimId ?? "manual-off";

            await _mqtt.PublishLedAsync(
                acceptor.AcceptorId,
                new LedCommandMessage
                {
                    State = "off",
                    ClaimId = claimId
                },
                _shutdown.Token);

            if (string.Equals(_activeLedAcceptorId, acceptor.AcceptorId, StringComparison.Ordinal))
            {
                _activeLedClaimId = null;
                _activeLedStationId = null;
                _activeLedAcceptorId = null;
                UpdateAcceptorGridState();
            }

            SetAddTimeStatus(
                pending is null
                    ? $"Sent LED OFF to {acceptor.AcceptorId}."
                    : $"Sent LED OFF to {acceptor.AcceptorId}. Active claim will still expire normally.",
                isError: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"LED-off command failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isReleasingAcceptor = false;
            UpdateAddTimeControls();
        }
    }

    // S49: server-initiated restart/reboot/shutdown of a station's Windows
    // host. All three share the same dispatch shape — confirmation dialog,
    // AdminCommandService call, status text update. Reboot/Shutdown use a
    // harder warning style ("Are you sure?" Yes/No with the Yes default
    // turned off) because they interrupt every paid session on that PC.
    private async void OnRestartClientClicked(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedRows();
        if (targets.Length == 0)
        {
            SetAddTimeStatus("Select one or more online stations first.", isError: true);
            return;
        }

        if (targets.Any(t => !t.IsOnline))
        {
            SetAddTimeStatus("Restart Client is blocked because the selection includes offline stations.", isError: true);
            return;
        }

        var stationList = FormatStationList(targets);
        var prompt = targets.Length == 1
            ? $"Restart the client app on {targets[0].StationId}? " +
              "Active paid sessions will be interrupted briefly while it reconnects."
            : $"Restart the client app on {targets.Length} selected station(s)?\n\n" +
              $"{stationList}\n\n" +
              "Active paid sessions will be interrupted briefly while each client reconnects.";
        var confirm = MessageBox.Show(this, prompt, "Restart Client",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        await DispatchAdminCommandsAsync(
            label: "Restart Client",
            targets: targets,
            invoke: (stationId, ct) => _adminCommandService.RestartClientAsync(stationId, "operator restart", ct));
    }

    private async void OnRebootPcClicked(object sender, RoutedEventArgs e)
    {
        var targets = SelectedOnlinePcRowsOrFail("Reboot PC");
        if (targets.Length == 0) return;

        var stationList = FormatStationList(targets);
        var prompt = targets.Length == 1
            ? $"REBOOT the Windows PC at {targets[0].StationId}?\n\n" +
              "Every paid session on that machine will be interrupted. " +
              "The PC will reboot after a 5 second grace period."
            : $"REBOOT {targets.Length} selected Windows PC(s)?\n\n" +
              $"{stationList}\n\n" +
              "Every paid session on those machines will be interrupted. " +
              "Each PC will reboot after a 5 second grace period.";
        var confirm = MessageBox.Show(this, prompt, "Reboot PC",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        await DispatchAdminCommandsAsync(
            label: "Reboot PC",
            targets: targets,
            invoke: (stationId, ct) => _adminCommandService.RebootPcAsync(stationId, "operator reboot", ct));
    }

    private async void OnShutdownPcClicked(object sender, RoutedEventArgs e)
    {
        var targets = SelectedOnlinePcRowsOrFail("Shut Down PC");
        if (targets.Length == 0) return;

        var stationList = FormatStationList(targets);
        var prompt = targets.Length == 1
            ? $"SHUT DOWN the Windows PC at {targets[0].StationId}?\n\n" +
              "Every paid session on that machine will be interrupted. " +
              "The PC will power off after a 5 second grace period and " +
              "will not come back until someone presses the power button."
            : $"SHUT DOWN {targets.Length} selected Windows PC(s)?\n\n" +
              $"{stationList}\n\n" +
              "Every paid session on those machines will be interrupted. " +
              "Each PC will power off after a 5 second grace period and " +
              "will not come back until someone presses the power button.";
        var confirm = MessageBox.Show(this, prompt, "Shut Down PC",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        await DispatchAdminCommandsAsync(
            label: "Shut Down PC",
            targets: targets,
            invoke: (stationId, ct) => _adminCommandService.ShutdownPcAsync(stationId, "operator shutdown", ct));
    }

    private async void OnShutdownIdlePcsClicked(object sender, RoutedEventArgs e)
    {
        var targets = GetOnlineLockedPcRows().ToArray();
        if (targets.Length == 0)
        {
            SetAddTimeStatus("No online locked Windows PCs to shut down.", isError: true);
            return;
        }

        var stationList = string.Join(", ", targets.Select(t => t.StationId));
        var prompt = $"Shut down {targets.Length} online locked Windows PC(s)?\n\n" +
                     $"{stationList}\n\n" +
                     "Active paid PCs are skipped. Each target will power off after a 5 second grace period.";
        var confirm = MessageBox.Show(this, prompt, "Shut Down Idle PCs",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        ShutdownIdlePcsButton.IsEnabled = false;

        var sent = new List<string>();
        var skipped = new List<string>();

        try
        {
            foreach (var target in targets)
            {
                var result = await _adminCommandService.ShutdownPcAsync(
                    target.StationId,
                    "operator shutdown idle pcs",
                    _shutdown.Token);

                if (result.Status == AdminCommandStatus.Published)
                {
                    sent.Add(target.StationId);
                    if (result.RequestId is not null)
                    {
                        _pendingCommandLabels[result.RequestId] = "Shut Down Idle PCs";
                    }
                }
                else
                {
                    skipped.Add($"{target.StationId} ({result.Status})");
                }
            }

            var suffix = skipped.Count == 0
                ? string.Empty
                : $" Skipped: {string.Join(", ", skipped)}.";
            SetAddTimeStatus(
                $"Shut Down Idle PCs: sent to {sent.Count} station(s): {string.Join(", ", sent)}.{suffix}",
                isError: sent.Count == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Shut Down Idle PCs failed: {ex.Message}", isError: true);
        }
        finally
        {
            UpdateAddTimeControls();
        }
    }

    private async void OnRemoveStationClicked(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedRows();
        if (targets.Length == 0)
        {
            SetAddTimeStatus("Select one or more offline stations to remove from the grid.", isError: true);
            return;
        }

        var onlineTargets = targets.Where(t => t.IsOnline).ToArray();
        if (onlineTargets.Length > 0)
        {
            SetAddTimeStatus(
                $"Remove blocked. Online station(s): {FormatStationList(onlineTargets)}.",
                isError: true);
            return;
        }

        var stationList = FormatStationList(targets);
        var prompt = $"Remove {targets.Length} offline station row(s) from the grid?\n\n" +
                     $"{stationList}\n\n" +
                     "Session and coin history will be kept.";
        var confirm = MessageBox.Show(this, prompt, "Remove Offline Stations",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        RemoveStationButton.IsEnabled = false;

        try
        {
            var removed = 0;
            foreach (var target in targets)
            {
                removed += await _stationRepository.HideOfflineAsync(target.StationId, _shutdown.Token);
            }

            await _stationRegistry.RefreshAsync(_shutdown.Token);

            SetAddTimeStatus(
                removed == 0
                    ? "No offline station rows were removed."
                    : $"Removed {removed} offline station row(s) from the grid.",
                isError: removed == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Remove station failed: {ex.Message}", isError: true);
        }
        finally
        {
            UpdateAddTimeControls();
        }
    }

    private async Task DispatchAdminCommandAsync(
        string label,
        Func<CancellationToken, Task<AdminCommandResult>> invoke)
    {
        try
        {
            var result = await invoke(_shutdown.Token);
            switch (result.Status)
            {
                case AdminCommandStatus.Published:
                    if (result.RequestId is not null)
                    {
                        _pendingCommandLabels[result.RequestId] = label;
                    }
                    SetAddTimeStatus(
                        $"{label}: sent to {result.StationId} (request {result.RequestId}) — awaiting acknowledgement…",
                        isError: false);
                    break;

                case AdminCommandStatus.StationOffline:
                    SetAddTimeStatus(
                        $"{label}: {result.StationId} is offline — command not sent.",
                        isError: true);
                    break;

                case AdminCommandStatus.StationUnknown:
                    SetAddTimeStatus(
                        $"{label}: {result.StationId} is not registered — command not sent.",
                        isError: true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"{label} failed: {ex.Message}", isError: true);
        }
    }

    // S49 follow-up: consume the client's command ack so the status line
    // confirms the station actually received the command, rather than leaving
    // it at "sent". For reboot/shutdown the client acks before it tears down,
    // so this typically lands a moment before the station drops offline.
    private Task HandleCommandAckAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (!MqttTopics.TryGetStationIdFromCommandAckTopic(topic, out var stationId))
        {
            return Task.CompletedTask;
        }

        AdminCommandAckMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<AdminCommandAckMessage>(
                payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.RequestId))
        {
            return Task.CompletedTask;
        }

        Dispatcher.Invoke(() =>
        {
            // Prefer the label captured at dispatch; fall back to a generic
            // prefix for acks whose request we no longer remember (e.g. after
            // a server restart).
            var label = _pendingCommandLabels.TryGetValue(message.RequestId, out var known)
                ? known
                : "Command";
            _pendingCommandLabels.Remove(message.RequestId);

            switch (message.Result)
            {
                case "rejected":
                    var why = string.IsNullOrWhiteSpace(message.Detail) ? "no reason given" : message.Detail;
                    SetAddTimeStatus($"{label}: {stationId} rejected the command — {why}.", isError: true);
                    break;

                case "acknowledged":
                case "completed":
                    SetAddTimeStatus($"{label}: {stationId} acknowledged the command.", isError: false);
                    break;

                default:
                    SetAddTimeStatus($"{label}: {stationId} responded ({message.Result}).", isError: false);
                    break;
            }
        });

        return Task.CompletedTask;
    }

    private StationGridRow? SelectedOnlineStationOrFail()
    {
        var selectedRows = GetSelectedRows();
        if (selectedRows.Length != 1)
        {
            SetAddTimeStatus("Select exactly one online station first.", isError: true);
            return null;
        }

        var selected = selectedRows[0];
        if (!selected.IsOnline)
        {
            SetAddTimeStatus($"{selected.StationId} is offline. Add/Clear Time is blocked.", isError: true);
            return null;
        }

        return selected;
    }

    private StationGridRow[] SelectedStationsOrFail(string actionName)
    {
        var selectedRows = GetSelectedRows();
        if (selectedRows.Length == 0)
        {
            SetAddTimeStatus($"Select one or more stations for {actionName}.", isError: true);
            return Array.Empty<StationGridRow>();
        }

        return selectedRows;
    }

    private StationGridRow[] SelectedOnlinePcRowsOrFail(string actionName)
    {
        var selectedRows = GetSelectedRows();
        if (selectedRows.Length == 0)
        {
            SetAddTimeStatus($"Select one or more online Windows PCs for {actionName}.", isError: true);
            return Array.Empty<StationGridRow>();
        }

        var invalidTargets = selectedRows
            .Where(row => !row.IsOnline || !string.Equals(row.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (invalidTargets.Length > 0)
        {
            SetAddTimeStatus(
                $"{actionName} is only for selected online Windows PCs. Invalid selection: {FormatStationList(invalidTargets)}.",
                isError: true);
            return Array.Empty<StationGridRow>();
        }

        return selectedRows;
    }

    private StationGridRow[] GetSelectedRows()
    {
        return StationGrid.SelectedItems
            .OfType<StationGridRow>()
            .OrderBy(row => _stations.IndexOf(row))
            .ToArray();
    }

    private static string FormatStationList(IEnumerable<StationGridRow> rows)
    {
        return string.Join(", ", rows.Select(row => row.StationId));
    }

    private static string BuildBatchStatus(
        string actionName,
        IReadOnlyCollection<string> applied,
        IReadOnlyCollection<string> skipped)
    {
        var message = applied.Count == 0
            ? $"{actionName}: no stations changed."
            : $"{actionName}: applied to {applied.Count} station(s): {string.Join(", ", applied)}.";

        if (skipped.Count > 0)
        {
            message += $" Skipped: {string.Join(", ", skipped)}.";
        }

        return message;
    }

    private static T? FindVisualParent<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private IEnumerable<StationGridRow> GetOnlineLockedPcRows()
    {
        foreach (var row in _stations)
        {
            if (!row.IsOnline ||
                !string.Equals(row.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remaining = _sessionService.GetLiveRemainingSeconds(row.StationId);
            if (remaining is null or <= 0)
            {
                yield return row;
            }
        }
    }

    private IEnumerable<StationGridRow> GetActivePaidStationRows()
    {
        foreach (var row in _stations)
        {
            if (_sessionService.GetLiveRemainingSeconds(row.StationId) is > 0)
            {
                yield return row;
            }
        }
    }

    private string FormatActiveStationList(IEnumerable<StationGridRow> rows)
    {
        return string.Join(
            "\n",
            rows.Select(row =>
            {
                var remaining = _sessionService.GetLiveRemainingSeconds(row.StationId);
                return $"- {row.StationId}: {FormatTimeRemaining(remaining)}";
            }));
    }

    private async Task DispatchAdminCommandsAsync(
        string label,
        IReadOnlyList<StationGridRow> targets,
        Func<string, CancellationToken, Task<AdminCommandResult>> invoke)
    {
        if (targets.Count == 1)
        {
            await DispatchAdminCommandAsync(
                label,
                ct => invoke(targets[0].StationId, ct));
            return;
        }

        var sent = new List<string>();
        var skipped = new List<string>();

        try
        {
            foreach (var target in targets)
            {
                var result = await invoke(target.StationId, _shutdown.Token);
                if (result.Status == AdminCommandStatus.Published)
                {
                    sent.Add(target.StationId);
                    if (result.RequestId is not null)
                    {
                        _pendingCommandLabels[result.RequestId] = label;
                    }
                }
                else
                {
                    skipped.Add($"{target.StationId} ({result.Status})");
                }
            }

            var suffix = skipped.Count == 0
                ? string.Empty
                : $" Skipped: {string.Join(", ", skipped)}.";
            SetAddTimeStatus(
                $"{label}: sent to {sent.Count} station(s): {string.Join(", ", sent)}.{suffix}",
                isError: sent.Count == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"{label} failed: {ex.Message}", isError: true);
        }
        finally
        {
            UpdateAddTimeControls();
        }
    }

    private async Task ApplyAddTimeAsync(StationGridRow selected, int secondsToAdd)
    {
        _isAddingTime = true;
        UpdateAddTimeControls();

        try
        {
            var result = await _adminTimeService.AddTimeAsync(
                selected.StationId,
                secondsToAdd,
                _shutdown.Token);

            switch (result.Status)
            {
                case AdminAddTimeStatus.Applied:
                    var remaining = _sessionService.GetLiveRemainingSeconds(selected.StationId)
                                    ?? result.Session!.RemainingSecondsAtAnchor;
                    SetAddTimeStatus(
                        $"Added {FormatDuration(secondsToAdd)} to {selected.StationId}. Remaining: {FormatDuration(remaining)}.",
                        isError: false);
                    break;

                case AdminAddTimeStatus.StationOffline:
                    SetAddTimeStatus($"{selected.StationId} is offline. Add Time was not applied.", isError: true);
                    break;

                case AdminAddTimeStatus.StationUnknown:
                    SetAddTimeStatus($"{selected.StationId} is not registered. Add Time was not applied.", isError: true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Add Time failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isAddingTime = false;
            UpdateAddTimeControls();
        }
    }

    private async Task ApplyAddTimeAsync(IReadOnlyList<StationGridRow> targets, int secondsToAdd)
    {
        _isAddingTime = true;
        UpdateAddTimeControls();

        try
        {
            var applied = new List<string>();
            var skipped = new List<string>();
            foreach (var target in targets)
            {
                var result = await _adminTimeService.AddTimeAsync(
                    target.StationId,
                    secondsToAdd,
                    _shutdown.Token);

                switch (result.Status)
                {
                    case AdminAddTimeStatus.Applied:
                        var remaining = _sessionService.GetLiveRemainingSeconds(target.StationId)
                                        ?? result.Session!.RemainingSecondsAtAnchor;
                        applied.Add($"{target.StationId} (remaining {FormatDuration(remaining)})");
                        break;

                    case AdminAddTimeStatus.StationOffline:
                        skipped.Add($"{target.StationId} (offline)");
                        break;

                    case AdminAddTimeStatus.StationUnknown:
                        skipped.Add($"{target.StationId} (not registered)");
                        break;
                }
            }

            SetAddTimeStatus(
                BuildBatchStatus($"Add {FormatDuration(secondsToAdd)}", applied, skipped),
                isError: applied.Count == 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Add Time failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isAddingTime = false;
            UpdateAddTimeControls();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _mqtt.ConnectAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ApplyStatus(new MqttConnectionStatus(false, "Could not connect to local broker.", ex.Message));
        }
    }

    private async Task PublishAbusePreventionConfigAsync(CancellationToken cancellationToken = default)
    {
        var config = _abusePrevention.CurrentConfig;
        var updatedAt = await _abusePreventionRepository.GetCurrentPublishedAtAsync(cancellationToken)
                        ?? FormatUtc(DateTimeOffset.UtcNow);
        var message = new AbusePreventionConfigMessage
        {
            Enabled = config.Enabled,
            MaxEmptyClaims = config.MaxEmptyClaims,
            BanMinutes = config.BanMinutes,
            UpdatedAt = updatedAt
        };
        await _mqtt.PublishAbusePreventionAsync(message, cancellationToken);
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _tickTimer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await _sessionService.TickAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => ApplyStatus(
                        new MqttConnectionStatus(_mqtt.IsConnected, "Session tick failed.", ex.Message)));
                }

                // Refresh the Time Remaining column AFTER the session tick
                // so any lock transition the tick just produced is visible
                // on the same cycle. Cheap — it's just a string update per
                // row, no DB read.
                Dispatcher.Invoke(RefreshStationTimeRemaining);
                await DispatchAutoIdleShutdownsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DispatchAutoIdleShutdownsAsync(CancellationToken cancellationToken)
    {
        var config = _pcPolicyConfig;
        var snapshots = Dispatcher.Invoke(() =>
            _stations
                .Select(row => new PcIdleStationSnapshot(
                    row.StationId,
                    row.DeviceType,
                    row.IsOnline,
                    _sessionService.GetLiveRemainingSeconds(row.StationId) ?? 0,
                    row.HasActiveClaim))
                .ToArray());

        var targets = _pcIdleShutdownTracker.Update(config, snapshots);
        if (targets.Count == 0)
        {
            return;
        }

        var sent = new List<string>();
        foreach (var stationId in targets)
        {
            var result = await _adminCommandService.ShutdownPcAsync(
                stationId,
                "auto idle shutdown",
                cancellationToken);

            if (result.Status == AdminCommandStatus.Published)
            {
                sent.Add(stationId);
                if (result.RequestId is not null)
                {
                    Dispatcher.Invoke(() =>
                        _pendingCommandLabels[result.RequestId] = "Auto Idle Shutdown");
                }
            }
        }

        if (sent.Count > 0)
        {
            Dispatcher.Invoke(() =>
                SetAddTimeStatus(
                    $"Auto Idle Shutdown: sent to {string.Join(", ", sent)}.",
                    isError: false));
        }
    }

    private async Task BackupLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CreateScheduledBackupIfDueAsync(cancellationToken);

            while (await _backupTimer.WaitForNextTickAsync(cancellationToken))
            {
                await CreateScheduledBackupIfDueAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CreateScheduledBackupIfDueAsync(CancellationToken cancellationToken)
    {
        if (HasBackupTodayUtc(_backupOptions.BackupRootPath))
        {
            Dispatcher.Invoke(() =>
            {
                BackupStatusText.Text = "Backup: already completed today";
                UpdateBackupControls();
            });
            return;
        }

        await RunBackupAsync("scheduled", showDialog: false, cancellationToken);
    }

    private async void OnBackupNowClicked(object sender, RoutedEventArgs e)
    {
        await RunBackupAsync("manual", showDialog: true, _shutdown.Token);
    }

    private void OnOpenBackupsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_backupOptions.BackupRootPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = _backupOptions.BackupRootPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open backup folder: {ex.Message}",
                "Backups",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RunBackupAsync(
        string reason,
        bool showDialog,
        CancellationToken cancellationToken)
    {
        if (!await _backupGate.WaitAsync(0, cancellationToken))
        {
            if (showDialog)
            {
                MessageBox.Show(
                    this,
                    "A backup is already running.",
                    "Backup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _isBackingUp = true;
            BackupStatusText.Text = "Backup: running...";
            UpdateBackupControls();
        });

        try
        {
            var result = await _backupService.CreateBackupAsync(
                _backupOptions with { Reason = reason },
                cancellationToken);

            Dispatcher.Invoke(() =>
            {
                BackupStatusText.Text = FormatBackupStatus(result);
                if (showDialog)
                {
                    MessageBox.Show(
                        this,
                        $"Backup completed.\n\nFolder:\n{result.BackupDirectory}",
                        "Backup",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                BackupStatusText.Text = $"Backup: failed - {ex.Message}";
                if (showDialog)
                {
                    MessageBox.Show(
                        this,
                        $"Backup failed: {ex.Message}",
                        "Backup",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            });
        }
        finally
        {
            _backupGate.Release();
            Dispatcher.Invoke(() =>
            {
                _isBackingUp = false;
                UpdateBackupControls();
            });
        }
    }

    private static string FormatBackupStatus(ServerBackupResult result)
    {
        var external = result.OffMachineCopyDirectory is null
            ? "external copy not configured"
            : "external copy OK";
        return $"Backup: {Path.GetFileName(result.BackupDirectory)} OK ({result.Validation.TableCount} tables, {external})";
    }

    private static bool HasBackupTodayUtc(string backupRootPath)
    {
        if (!Directory.Exists(backupRootPath))
        {
            return false;
        }

        var prefix = $"backup-{DateTimeOffset.UtcNow:yyyyMMdd}";
        return Directory
            .EnumerateDirectories(backupRootPath, $"{prefix}*", SearchOption.TopDirectoryOnly)
            .Any(directory => File.Exists(Path.Combine(directory, "manifest.json")));
    }

    private static ServerBackupOptions CreateBackupOptions(string databasePath, string reason)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var databaseDirectory = Path.GetDirectoryName(databasePath) ?? Environment.CurrentDirectory;
        var mediaDirectory = Path.Combine(databaseDirectory, "media");

        return new ServerBackupOptions(
            DatabasePath: databasePath,
            BackupRootPath: RentalDatabase.GetDefaultBackupRootPath())
        {
            Reason = reason,
            RetentionCount = 14,
            ConfigFilePaths = new[]
            {
                Path.Combine(baseDirectory, "appsettings.json"),
                Path.Combine(baseDirectory, "appsettings.Local.json")
            },
            AssetDirectoryPaths = new[]
            {
                mediaDirectory
            },
            OffMachineCopyRootPath = GetOffMachineBackupTarget()
        };
    }

    private static string? GetOffMachineBackupTarget()
    {
        var target = Environment.GetEnvironmentVariable("RENTAL_BACKUP_COPY_TARGET");
        return string.IsNullOrWhiteSpace(target) ? null : target.Trim();
    }

    private void OnMqttStatusChanged(object? sender, MqttConnectionStatus status)
    {
        Dispatcher.Invoke(() => ApplyStatus(status));
    }

    private void OnStationsChanged(object? sender, IReadOnlyList<StationRecord> stations)
    {
        Dispatcher.Invoke(() =>
        {
            var selectedIds = GetSelectedRows()
                .Select(row => row.StationId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _stations.Clear();

            foreach (var station in stations)
            {
                var row = new StationGridRow(station);
                // Seed TimeRemainingText immediately so the new row doesn't
                // briefly flash "—" before the next tick paints it.
                var remaining = _sessionService.GetLiveRemainingSeconds(station.StationId);
                row.TimeRemainingText = FormatTimeRemaining(remaining);
                row.OperatorStatusText = FormatOperatorStationStatus(row, remaining);
                row.IsLocked = remaining is null or <= 0;
                row.HasActiveClaim = row.IsOnline &&
                    string.Equals(station.StationId, _activeLedStationId, StringComparison.Ordinal);
                row.AcceptorStateText = _acceptorGridStateText;
                _stations.Add(row);
            }

            if (selectedIds.Count > 0)
            {
                StationGrid.SelectedItems.Clear();
                foreach (var row in _stations.Where(row => selectedIds.Contains(row.StationId)))
                {
                    StationGrid.SelectedItems.Add(row);
                }
            }

            UpdateAddTimeControls();
            UpdateSelectedStationPanel();
        });
    }

    /// <summary>
    /// Refreshes the Time Remaining column on every visible station row
    /// from SessionService. Called from the 1 Hz tick loop so the column
    /// counts down in real time between server publishes.
    /// </summary>
    private void RefreshStationTimeRemaining()
    {
        foreach (var row in _stations)
        {
            var remaining = _sessionService.GetLiveRemainingSeconds(row.StationId);
            row.TimeRemainingText = FormatTimeRemaining(remaining);
            row.OperatorStatusText = FormatOperatorStationStatus(row, remaining);
            row.IsLocked = remaining is null or <= 0;
            row.HasActiveClaim = row.IsOnline &&
                string.Equals(row.StationId, _activeLedStationId, StringComparison.Ordinal);
            row.AcceptorStateText = _acceptorGridStateText;
        }

        UpdateSelectedStationPanel();
    }

    /// <summary>
    /// Human-readable formatting for the Time Remaining column.
    /// "—" when the station has no session, "0s" when locked at zero,
    /// "Hh Mm" for active sessions over an hour, "M:SS" for short.
    /// </summary>
    private static string FormatTimeRemaining(int? remainingSec)
    {
        if (remainingSec is null)
        {
            return "—";
        }

        var total = remainingSec.Value;
        if (total <= 0)
        {
            return "0s";
        }

        var hours = total / 3600;
        var minutes = (total % 3600) / 60;
        var seconds = total % 60;

        if (hours > 0)
        {
            return $"{hours}h {minutes:D2}m {seconds:D2}s";
        }

        if (minutes > 0)
        {
            return $"{minutes}m {seconds:D2}s";
        }

        return $"{seconds}s";
    }

    private static string FormatOperatorStationStatus(StationGridRow row, int? remainingSec)
    {
        if (!row.IsOnline)
        {
            return "OFFLINE";
        }

        if (remainingSec is null)
        {
            return "ONLINE";
        }

        if (remainingSec <= 0)
        {
            return "LOCKED";
        }

        return $"ACTIVE - {FormatCompactDuration(remainingSec.Value)}";
    }

    private static string FormatCompactDuration(int totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;

        if (hours > 0)
        {
            return $"{hours:D2}:{minutes:D2}";
        }

        return $"{minutes:D2}:{totalSeconds % 60:D2}";
    }

    private void OnStationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAddTimeControls();
        UpdateSelectedStationPanel();

        var selectedRows = GetSelectedRows();
        if (selectedRows.Length == 0)
        {
            SetAddTimeStatus("Select one or more stations to add, remove, or clear time.", isError: false);
            return;
        }

        if (selectedRows.Length == 1)
        {
            var selected = selectedRows[0];
            SetAddTimeStatus(
                selected.IsOnline
                    ? $"Ready to edit time for {selected.StationId}."
                    : $"{selected.StationId} is offline. Time changes will sync on reconnect.",
                isError: false);
            return;
        }

        var onlinePcCount = selectedRows.Count(row =>
            row.IsOnline &&
            string.Equals(row.DeviceType, "pc", StringComparison.OrdinalIgnoreCase));
        SetAddTimeStatus(
            $"{selectedRows.Length} stations selected. Add/Remove/Clear will apply to all selected rows. Reboot/Shut Down PC targets {onlinePcCount} selected online PC(s).",
            isError: false);
    }

    private void OnStationGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _stationDragStartPoint = e.GetPosition(StationGrid);
        _stationDragSource = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext as StationGridRow;
    }

    private void OnStationGridMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _stationDragStartPoint is null ||
            _stationDragSource is null)
        {
            return;
        }

        var current = e.GetPosition(StationGrid);
        var movedEnough =
            Math.Abs(current.X - _stationDragStartPoint.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _stationDragStartPoint.Value.Y) > SystemParameters.MinimumVerticalDragDistance;
        if (!movedEnough)
        {
            return;
        }

        DragDrop.DoDragDrop(
            StationGrid,
            new DataObject(typeof(StationGridRow), _stationDragSource),
            DragDropEffects.Move);
        _stationDragStartPoint = null;
        _stationDragSource = null;
    }

    private void OnStationGridDragOver(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(typeof(StationGridRow)) as StationGridRow;
        var target = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext as StationGridRow;
        e.Effects = source is not null && target is not null && CanTransferTime(source, target, out _)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnStationGridDrop(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(typeof(StationGridRow)) as StationGridRow;
        var target = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext as StationGridRow;
        if (source is null || target is null)
        {
            return;
        }

        if (!CanTransferTime(source, target, out var reason))
        {
            SetAddTimeStatus(reason, isError: true);
            return;
        }

        await ConfirmAndTransferTimeAsync(source, target);
    }

    private bool CanTransferTime(StationGridRow source, StationGridRow target, out string reason)
    {
        if (ReferenceEquals(source, target) ||
            string.Equals(source.StationId, target.StationId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Drag to a different locked PC to transfer time.";
            return false;
        }

        if (!string.Equals(source.DeviceType, "pc", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Time transfer is currently limited to Windows PCs.";
            return false;
        }

        if (!source.IsOnline || !target.IsOnline)
        {
            reason = "Both PCs must be online before transferring time.";
            return false;
        }

        if (source.HasActiveClaim || target.HasActiveClaim)
        {
            reason = "Wait for the coin claim window to finish before transferring time.";
            return false;
        }

        var sourceRemaining = _sessionService.GetLiveRemainingSeconds(source.StationId);
        if (sourceRemaining is null or <= 0)
        {
            reason = $"{source.StationId} has no time to transfer.";
            return false;
        }

        var targetRemaining = _sessionService.GetLiveRemainingSeconds(target.StationId);
        if (targetRemaining is > 0)
        {
            reason = $"{target.StationId} already has active time. Transfer only to a locked/no-time PC.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryGetSelectedTransferPair(
        out StationGridRow source,
        out StationGridRow target,
        out string reason)
    {
        source = null!;
        target = null!;
        reason = string.Empty;

        var selected = GetSelectedRows();
        if (selected.Length != 2)
        {
            reason = "Select exactly two online PCs: one with time, one locked/no-time.";
            return false;
        }

        var invalid = selected
            .Where(row => !row.IsOnline || !string.Equals(row.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (invalid.Length > 0)
        {
            reason = $"Transfer Time only works between online PCs. Invalid: {FormatStationList(invalid)}.";
            return false;
        }

        var withTime = selected
            .Where(row => (_sessionService.GetLiveRemainingSeconds(row.StationId) ?? 0) > 0)
            .ToArray();
        var withoutTime = selected
            .Where(row => (_sessionService.GetLiveRemainingSeconds(row.StationId) ?? 0) <= 0)
            .ToArray();

        if (withTime.Length != 1 || withoutTime.Length != 1)
        {
            reason = "Select one PC with active time and one locked/no-time PC.";
            return false;
        }

        source = withTime[0];
        target = withoutTime[0];
        return CanTransferTime(source, target, out reason);
    }

    private async Task ConfirmAndTransferTimeAsync(StationGridRow source, StationGridRow target)
    {
        var remaining = _sessionService.GetLiveRemainingSeconds(source.StationId) ?? 0;
        var confirm = MessageBox.Show(
            this,
            $"Transfer {FormatDuration(remaining)} from {source.StationId} to {target.StationId}?\n\n" +
            $"{source.StationId} will lock at 0. {target.StationId} will receive the time.",
            "Confirm Time Transfer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            SetAddTimeStatus("Time transfer cancelled.", isError: false);
            return;
        }

        _isAddingTime = true;
        UpdateAddTimeControls();

        try
        {
            var result = await _adminTimeService.TransferTimeAsync(
                source.StationId,
                target.StationId,
                _shutdown.Token);

            switch (result.Status)
            {
                case AdminTransferTimeStatus.Applied:
                    SetAddTimeStatus(
                        $"Transferred {FormatDuration(result.SecondsTransferred)} from {source.StationId} to {target.StationId}.",
                        isError: false);
                    break;
                case AdminTransferTimeStatus.TargetHasTime:
                    SetAddTimeStatus($"{target.StationId} already has time. Transfer blocked.", isError: true);
                    break;
                case AdminTransferTimeStatus.NoSourceTime:
                    SetAddTimeStatus($"{source.StationId} has no time to transfer.", isError: true);
                    break;
                case AdminTransferTimeStatus.SourceOffline:
                case AdminTransferTimeStatus.TargetOffline:
                    SetAddTimeStatus("Both stations must be online before transferring time.", isError: true);
                    break;
                default:
                    SetAddTimeStatus($"Time transfer blocked ({result.Status}).", isError: true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetAddTimeStatus($"Time transfer failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isAddingTime = false;
            UpdateAddTimeControls();
        }
    }

    private void UpdateSelectedStationPanel()
    {
        var selectedRows = GetSelectedRows();
        if (selectedRows.Length == 0)
        {
            SelectedStationTitleText.Text = "Select a station";
            SelectedStationDetailText.Text = "No station selected.";
            return;
        }

        if (selectedRows.Length > 1)
        {
            var onlineCount = selectedRows.Count(row => row.IsOnline);
            var offlineCount = selectedRows.Length - onlineCount;
            var pcCount = selectedRows.Count(row =>
                string.Equals(row.DeviceType, "pc", StringComparison.OrdinalIgnoreCase));

            SelectedStationTitleText.Text = $"{selectedRows.Length} stations selected";
            SelectedStationDetailText.Text =
                $"Selected: {FormatStationList(selectedRows)}\n" +
                $"Online: {onlineCount}\n" +
                $"Offline: {offlineCount}\n" +
                $"PCs: {pcCount}";
            return;
        }

        var selected = selectedRows[0];
        SelectedStationTitleText.Text =
            $"{selected.StationName} - {selected.OperatorStatusText}";

        var remaining = _sessionService.GetLiveRemainingSeconds(selected.StationId);
        SelectedStationDetailText.Text =
            $"Station ID: {selected.StationId}\n" +
            $"Device type: {selected.DeviceType}\n" +
            $"Online: {(selected.IsOnline ? "Yes" : "No")}\n" +
            $"Time remaining: {FormatTimeRemaining(remaining)}\n" +
            $"Acceptor: {selected.AcceptorStateText}\n" +
            $"Last seen: {selected.LastSeenAt ?? "never"}\n" +
            $"App version: {selected.AppVersion ?? "unknown"}";
    }

    private void OnAcceptorsChanged(object? sender, IReadOnlyList<AcceptorRecord> acceptors)
    {
        Dispatcher.Invoke(() =>
        {
            _acceptors = acceptors.ToArray();
            UpdateAcceptorGridState();

            if (acceptors.Count == 0)
            {
                AcceptorStatusText.Text = "Acceptor: (none registered)";
                UpdateAddTimeControls();
                return;
            }

            // One coin acceptor in this shop. Multi-acceptor would warrant a grid.
            var first = acceptors[0];
            var rssi = first.LastRssi.HasValue ? $" (RSSI {first.LastRssi} dBm)" : string.Empty;
            AcceptorStatusText.Text = first.IsOnline
                ? $"Acceptor {first.AcceptorId}: online{rssi}"
                : $"Acceptor {first.AcceptorId}: offline";

            UpdateAddTimeControls();
        });
    }

    private void OnClaimGranted(object? sender, ClaimRecord claim)
    {
        Dispatcher.Invoke(() =>
        {
            _activeLedClaimId = claim.ClaimId;
            _activeLedStationId = claim.StationId;
            _activeLedAcceptorId = claim.AcceptorId;
            UpdateAcceptorGridState();
        });
    }

    private void OnClaimResolved(object? sender, ClaimRecord claim)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.Equals(_activeLedClaimId, claim.ClaimId, StringComparison.Ordinal))
            {
                _activeLedClaimId = null;
                _activeLedStationId = null;
                _activeLedAcceptorId = null;
            }

            UpdateAcceptorGridState();
        });
    }

    private void UpdateAcceptorGridState()
    {
        _acceptorGridStateText = _activeLedStationId is not null
            ? $"LED ON - {_activeLedStationId}"
            : _acceptors.Count == 0
                ? "NO ACCEPTOR"
                : _acceptors.Any(a => a.IsOnline)
                    ? "READY"
                    : "OFFLINE";

        foreach (var row in _stations)
        {
            row.AcceptorStateText = _acceptorGridStateText;
        }

        UpdateSelectedStationPanel();
    }

    private void OnSessionChanged(object? sender, SessionState state)
    {
        Dispatcher.Invoke(() =>
        {
            SessionInfoText.Text = state.IsLocked
                ? $"Session {state.StationId}: LOCKED"
                : $"Session {state.StationId}: {FormatTimeRemaining(state.RemainingSecondsAtAnchor)} remaining";
            RefreshStationTimeRemaining();
        });
    }

    private void ApplyStatus(MqttConnectionStatus status)
    {
        StatusIndicator.Fill = status.IsConnected
            ? new SolidColorBrush(Color.FromRgb(46, 125, 50))
            : new SolidColorBrush(Color.FromRgb(198, 40, 40));

        StatusText.Text = status.IsConnected ? "Server online" : "Server offline";

        var detail = string.IsNullOrWhiteSpace(status.Detail)
            ? "Broker: 127.0.0.1:1883"
            : status.Detail;
        var verbose = $"{status.Message}\n{detail}\nStations: {_stations.Count}\nAcceptor: {_acceptorGridStateText}";
        if (!string.IsNullOrWhiteSpace(_discoveryDiagnosticsText))
        {
            verbose += $"\n{_discoveryDiagnosticsText}";
        }
        DetailText.Text = verbose;
        VerboseStatusIcon.ToolTip = verbose;
    }

    private bool TryGetAddTimeSeconds(out int seconds, out string error)
    {
        seconds = 0;
        error = string.Empty;

        var input = CustomTimeAmountBox.Text.Trim();
        if (!decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            error = "Enter a number, then choose sec, min, or hr.";
            return false;
        }

        if (amount <= 0)
        {
            error = "Enter a duration greater than 0.";
            return false;
        }

        var unit = (CustomTimeUnitCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "min";
        var rawSeconds = unit switch
        {
            "sec" => amount,
            "hr" => amount * 3600m,
            _ => amount * 60m
        };

        if (rawSeconds > 24m * 3600m)
        {
            error = "Capped at 24 hours per click.";
            return false;
        }

        seconds = (int)Math.Ceiling(rawSeconds);
        if (seconds <= 0) { error = "Enter a duration greater than 0."; return false; }
        return true;
    }

#if false
    private bool TryGetAddTimeSecondsOld(out int seconds, out string error)
    {
        seconds = 0;
        error = string.Empty;

        // Check if a preset ComboBoxItem is selected (Tag carries seconds)
        if (CustomTimeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tagStr &&
            int.TryParse(tagStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var presetSec))
        {
            seconds = presetSec;
            return true;
        }

        // Free-text entry — parse HH:MM:SS, MM:SS, or plain seconds
        var input = CustomTimeCombo.Text.Trim();
        if (input.Length == 0)
        {
            error = "Enter a duration or pick from the list.";
            return false;
        }

        var parts = input.Split(':');
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var s))
            {
                error = "Invalid format. Use HH:MM:SS (e.g. 00:30:00).";
                return false;
            }
            seconds = (h * 3600) + (m * 60) + s;
        }
        else if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var s))
            {
                error = "Invalid format. Use MM:SS (e.g. 05:00).";
                return false;
            }
            seconds = (m * 60) + s;
        }
        else if (int.TryParse(input, out var plainSeconds))
        {
            seconds = plainSeconds;
        }
        else
        {
            error = "Enter HH:MM:SS, MM:SS, plain seconds, or pick a preset.";
            return false;
        }

        if (seconds <= 0) { error = "Enter a duration greater than 0."; return false; }
        if (seconds > 24 * 3600) { error = "Capped at 24 hours per click."; return false; }
        return true;
    }

#endif
    private void UpdateAddTimeControls()
    {
        var selectedRows = GetSelectedRows();
        var selectedOnlinePcRows = selectedRows
            .Where(row => row.IsOnline &&
                          string.Equals(row.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var canAct =
            !_isAddingTime &&
            selectedRows.Length > 0;

        AddTimeButton.IsEnabled = canAct;
        // S32: keep the quick-action buttons and the typed-minutes button
        // gated on the same condition. Clear Time follows the same gate
        // because it also targets the selected online station, but its
        // visual treatment in XAML (red FG) is the destructive-action cue.
        AddTime30Button.IsEnabled = canAct;
        AddTime60Button.IsEnabled = canAct;
        AddTime120Button.IsEnabled = canAct;
        // Typed-amount Remove Time mirrors Add Time on the same textbox.
        RemoveTimeButton.IsEnabled = canAct;
        // Remove Time presets — orange FG cue, same gating predicate.
        RemoveTime30Button.IsEnabled = canAct;
        RemoveTime60Button.IsEnabled = canAct;
        RemoveTime120Button.IsEnabled = canAct;
        ClearTimeButton.IsEnabled = canAct;
        TransferTimeButton.IsEnabled =
            !_isAddingTime &&
            selectedRows.Length == 2 &&
            TryGetSelectedTransferPair(out _, out _, out _);
        HeaderTransferTimeButton.IsEnabled = TransferTimeButton.IsEnabled;
        ReleaseAcceptorButton.IsEnabled =
            !_isReleasingAcceptor &&
            _acceptors.Any(a => a.IsOnline);
        OpenStationButton.IsEnabled =
            selectedRows is [{ IsOnline: true }];

        // S49: remote restart/reboot/shutdown follow the same online-only
        // gate. We don't separately track an "in flight" flag because each
        // press kills the target station — re-pressing during the OS grace
        // period is harmless; the audit row records every press.
        var canRestartClients = selectedRows.Length > 0 && selectedRows.All(row => row.IsOnline);
        var canPowerSelectedPcs =
            selectedRows.Length > 0 &&
            selectedOnlinePcRows.Length == selectedRows.Length;
        RestartClientButton.IsEnabled = canRestartClients;
        RebootPcButton.IsEnabled = canPowerSelectedPcs;
        ShutdownPcButton.IsEnabled = canPowerSelectedPcs;
        ShutdownIdlePcsButton.IsEnabled = GetOnlineLockedPcRows().Any();
        RemoveStationButton.IsEnabled =
            selectedRows.Length > 0 &&
            selectedRows.All(row => !row.IsOnline);
    }

    private void UpdateBackupControls()
    {
        BackupNowButton.IsEnabled = !_isBackingUp && !_shutdown.IsCancellationRequested;
        OpenBackupsButton.IsEnabled = !_shutdown.IsCancellationRequested;
    }

    private void SetAddTimeStatus(string message, bool isError)
    {
        AddTimeStatusText.Text = message;
        AddTimeStatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(198, 40, 40)
            : Color.FromRgb(46, 125, 50));
    }

    private static string FormatDuration(int totalSeconds)
    {
        var totalMinutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return seconds == 0
            ? $"{totalMinutes} min"
            : $"{totalMinutes}:{seconds:D2}";
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _shutdown.Cancel();
        _tickTimer.Dispose();
        _backupTimer.Dispose();

        if (_tickLoop is not null)
        {
            try
            {
                await _tickLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_backupLoop is not null)
        {
            try
            {
                await _backupLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _mqtt.DisposeAsync();
        _udpBroadcaster?.Dispose();
        _mdnsAdvertiser?.Dispose();
        _httpServer?.Dispose();
        _onlineUpdatePoller?.Dispose();
        _backupGate.Dispose();
        _shutdown.Dispose();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    // ---------- S50a server self-update ----------

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new MqttClientFactory().CreateMqttClient();
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_mqttConfig.Host, _mqttConfig.Port)
                .Build();
            await client.ConnectAsync(options, _shutdown.Token);

            var received = new TaskCompletionSource<string?>();
            client.ApplicationMessageReceivedAsync += args =>
            {
                var json = Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray());
                received.TrySetResult(json);
                return Task.CompletedTask;
            };

            await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(UpdateManifest.MqttTopic)
                    .Build(), _shutdown.Token);

            var timeout = Task.Delay(3000, _shutdown.Token);
            var winner = await Task.WhenAny(received.Task, timeout);
            await client.DisconnectAsync(); client.Dispose();

            if (winner == timeout)
            {
                MessageBox.Show(this, "No update manifest published yet.",
                    "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(received.Task.Result!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest?.Server is null)
            {
                MessageBox.Show(this, "No server update in manifest.",
                    "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Read the FileVersion from the running EXE itself, set during
            // publish via `dotnet publish /p:Version=X.Y.Z`. Assembly.GetName()
            // returns the AssemblyVersion attribute which defaults to 1.0.0.0
            // on every build that does not explicitly set it — so without this,
            // a successfully-applied update still reads its predecessor's
            // version and "Check for Updates" would prompt the operator again.
            var (currentVersion, publishedIsNewer) = CompareWithRunningVersion(manifest.Server.Version);
            if (!publishedIsNewer)
            {
                MessageBox.Show(this, $"Already up to date ({currentVersion}).",
                    "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var activeStations = GetActivePaidStationRows().ToArray();
            var msg = $"Update v{manifest.Server.Version} available (current: {currentVersion}).\n" +
                      $"Download size: {manifest.Server.SizeBytes / 1024 / 1024} MB";
            var title = "Update Available";
            var icon = MessageBoxImage.Question;

            if (activeStations.Length > 0)
            {
                title = "Update During Active Sessions";
                icon = MessageBoxImage.Warning;
                msg =
                    $"Risky: {activeStations.Length} station(s) still have active paid time.\n\n" +
                    $"{FormatActiveStationList(activeStations)}\n\n" +
                    "Updating now will restart the server app. Existing clients should keep counting down locally, " +
                    "but coin insert, admin commands, and grid updates may pause while the server restarts.\n\n" +
                    $"{msg}\n\nProceed anyway?";
            }
            else
            {
                msg = $"{msg}\n\nProceed?";
            }

            if (MessageBox.Show(this, msg, title,
                    MessageBoxButton.YesNo, icon, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync(manifest.Server.Url, _shutdown.Token);

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, manifest.Server.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"SHA-256 mismatch.\nExpected: {manifest.Server.Sha256[..16]}...\nGot: {actualHash[..16]}...",
                    "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var exePath = Environment.ProcessPath!;
            var newExePath = Path.ChangeExtension(exePath, ".exe.new");
            await File.WriteAllBytesAsync(newExePath, bytes, _shutdown.Token);

            App.LaunchSwapBatch(exePath, newExePath);
            Dispatcher.InvokeAsync(() => Application.Current.Shutdown(0));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Update failed: {ex.Message}",
                "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Returns the running EXE's stamped FileVersion and whether the manifest's
    // published version is strictly newer. System.Version handles 3-vs-4-part
    // and double-digit segments correctly (1.0.10 > 1.0.2), unlike string
    // comparison. If the running version cannot be determined (no Version
    // resource, default 0.0.0.0), we treat the published as "newer" so an
    // operator can still install manually.
    private static (string Current, bool PublishedIsNewer) CompareWithRunningVersion(string published)
    {
        Version? currentParsed = null;
        string currentDisplay = "unknown";
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var raw = info.FileVersion;
            if (!string.IsNullOrWhiteSpace(raw) && Version.TryParse(raw, out var v))
            {
                if (!(v.Major == 0 && v.Minor == 0 && v.Build <= 0 && v.Revision <= 0))
                {
                    currentParsed = v;
                    currentDisplay = v.ToString();
                }
            }
        }
        if (!Version.TryParse(published, out var pv))
            return (currentDisplay, false);
        if (currentParsed is null)
            return (currentDisplay, true);
        var normPv = new Version(pv.Major, pv.Minor, Math.Max(pv.Build, 0), Math.Max(pv.Revision, 0));
        var normCv = new Version(currentParsed.Major, currentParsed.Minor, Math.Max(currentParsed.Build, 0), Math.Max(currentParsed.Revision, 0));
        return (currentDisplay, normPv > normCv);
    }

    // Running EXE FileVersion (set at publish via /p:Version), for the header
    // version indicator. "unknown" on a dev build with no stamped version.
    private static string GetRunningVersionDisplay()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var raw = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                if (!string.IsNullOrWhiteSpace(raw)) return raw;
            }
        }
        catch { /* fall through to unknown */ }
        return "unknown";
    }

    // ---------- S46 LAN discovery ----------

    private void StartDiscoveryBroadcaster()
    {
        // advertiseHost (config override) wins when set — the escape hatch
        // for a multi-homed server where auto-detection could legitimately
        // pick either NIC. Otherwise auto-detect, then fall back to the
        // broker host itself (existing behavior).
        var candidates = DescribeCandidateAdapters();
        foreach (var line in candidates)
        {
            Debug.WriteLine($"[discovery] candidate adapter: {line}");
        }

        var advertiseHostOverride = _mqttConfig.AdvertiseHost;
        var usingOverride = !string.IsNullOrWhiteSpace(advertiseHostOverride);
        var lanIp = usingOverride ? advertiseHostOverride! : GetLanIpAddress() ?? _mqttConfig.Host;

        var chosenSource = usingOverride ? "advertiseHost config override" : "auto-detected/fallback";
        Debug.WriteLine($"[discovery] chosen advertise IP: {lanIp} (source: {chosenSource})");

        // Mirror the same lines into the operator-visible tooltip (VerboseStatusIcon,
        // via ApplyStatus) — Debug.WriteLine above is invisible in a Release build,
        // and this is the one diagnostic an operator needs on a multi-homed server
        // to explain why a Wi-Fi device (tablet/coin box) picked .200 vs .201.
        var diagnosticsLines = new List<string> { "Discovery:" };
        diagnosticsLines.AddRange(candidates.Select(line => $"  adapter: {line}"));
        diagnosticsLines.Add($"  chosen advertise IP: {lanIp} (source: {chosenSource})");
        _discoveryDiagnosticsText = string.Join("\n", diagnosticsLines);

        // Force the tooltip to pick up the discovery lines now — ApplyStatus
        // already ran once during connect (before discovery started), so
        // without this the operator wouldn't see them until the next
        // reconnect/status event.
        ApplyStatus(new MqttConnectionStatus(
            _mqtt.IsConnected,
            _mqtt.IsConnected ? "Server online" : "Server offline"));

        _udpBroadcaster = new UdpDiscoveryBroadcaster(lanIp, _mqttConfig.Port, "1.1.0");
        _udpBroadcaster.Start();
        Debug.WriteLine($"[discovery] UDP broadcaster started — advertising {lanIp}:{_mqttConfig.Port}");

        // mDNS advertiser runs alongside UDP: multicast reaches Wi-Fi devices
        // (tablets, ESP32) that the wired-only limited broadcast can't. Wrapped
        // because mDNS is best-effort — a 5353 bind failure must not take the
        // server down; UDP discovery + fixed-IP fallback still work.
        try
        {
            _mdnsAdvertiser = new MdnsDiscoveryAdvertiser(lanIp, _mqttConfig.Port, "1.1.0");
            _mdnsAdvertiser.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[discovery] mDNS advertiser failed to start: {ex.Message}");
        }
    }

    // Enumerates candidate adapters + their IPv4s for startup diagnostics —
    // on a multi-homed host this is what an operator needs to see to explain
    // why one NIC got chosen over another (or to justify setting
    // advertiseHost explicitly).
    private static List<string> DescribeCandidateAdapters()
    {
        var lines = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var ipProps = ni.GetIPProperties();
            var hasGateway = ipProps.GatewayAddresses.Count > 0;
            var ipv4s = ipProps.UnicastAddresses
                .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                .Select(ip => ip.Address.ToString());

            foreach (var ipv4 in ipv4s)
            {
                lines.Add($"{ni.Name} ({ni.Description}) -> {ipv4} [gateway={hasGateway}]");
            }
        }
        return lines;
    }

    private static string? GetLanIpAddress()
    {
        // Adapter names/descriptions that identify virtual NICs we never want
        // to advertise (Hyper-V/vEthernet/VMware/VirtualBox hosts show up as
        // "Up" with a real-looking IPv4, but they aren't the LAN path).
        string[] virtualAdapterMarkers = { "Hyper-V", "vEthernet", "VMware", "VirtualBox", "Loopback" };

        var noGatewayFallback = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            // Skip loopback, tunnel, and virtual adapters.
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (virtualAdapterMarkers.Any(marker =>
                    ni.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var ipProps = ni.GetIPProperties();
            var hasGateway = ipProps.GatewayAddresses.Count > 0;

            foreach (var ip in ipProps.UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(ip.Address))
                {
                    continue;
                }

                // Skip APIPA (link-local 169.254.x.x) — a DHCP failure
                // address, never the real LAN route.
                var octets = ip.Address.GetAddressBytes();
                if (octets.Length == 4 && octets[0] == 169 && octets[1] == 254)
                {
                    continue;
                }

                // Prefer an adapter with a default gateway — that's the real
                // LAN route on a multi-homed host. Stash gateway-less
                // candidates as a fallback in case nothing has a gateway.
                if (hasGateway)
                {
                    return ip.Address.ToString();
                }

                noGatewayFallback.Add(ip.Address.ToString());
            }
        }

        return noGatewayFallback.Count > 0 ? noGatewayFallback[0] : null;
    }

    private static ServerMqttConfig LoadServerMqttConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ServerMqttConfig();
        }

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new ServerMqttConfig
        {
            Host = ReadStringProperty(root, "host", "127.0.0.1"),
            Port = ReadInt32Property(root, "port", 1883),
            Username = ReadOptionalStringProperty(root, "username"),
            Password = ReadOptionalStringProperty(root, "password"),
            AdvertiseHost = ReadOptionalStringProperty(root, "advertiseHost")
        };

        static string ReadStringProperty(JsonElement root, string prop, string defaultValue)
        {
            if (root.TryGetProperty("broker", out var broker) &&
                broker.TryGetProperty(prop, out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? defaultValue;
            }
            return defaultValue;
        }

        static int ReadInt32Property(JsonElement root, string prop, int defaultValue)
        {
            if (root.TryGetProperty("broker", out var broker) &&
                broker.TryGetProperty(prop, out var el) &&
                el.ValueKind == JsonValueKind.Number &&
                el.TryGetInt32(out var val))
            {
                return val;
            }
            return defaultValue;
        }

        static string? ReadOptionalStringProperty(JsonElement root, string prop)
        {
            if (root.TryGetProperty("broker", out var broker) &&
                broker.TryGetProperty(prop, out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            return null;
        }
    }

}
