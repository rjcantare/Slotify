using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using RentalServer.Core.Acceptors;
using RentalServer.Core.Admin;
using RentalServer.Core.Claims;
using RentalServer.Core.Rates;
using RentalServer.Core.Sessions;
using RentalServer.Core.PcPolicy;
using RentalServer.Core.TabletApps;
using RentalServer.Core.TabletAdmin;
using RentalShared.Mqtt;

namespace RentalServer.Core.Mqtt;

// Owns the MQTT connection. Inbound messages are routed to handler delegates that
// the host wires up — that way the service can implement publisher interfaces
// (ISessionPublisher, ICreditPublisher, IClaimResultPublisher, etc.) without forming
// a constructor cycle with the services that consume those publishers.
public sealed class RentalServerMqttService
    : IAsyncDisposable, ISessionPublisher, ICreditPublisher,
      IClaimResultPublisher, IPulseAckPublisher, ILedPublisher, IRatesConfigPublisher,
      ITabletAppConfigPublisher, ITabletAdminConfigPublisher, IPcPolicyConfigPublisher,
      IAdminCommandPublisher
{
    public static readonly IReadOnlyList<string> SubscribedTopics =
    [
        MqttTopics.StationStatusWildcard,
        MqttTopics.StationSessionClearWildcard,
        MqttTopics.StationClaimRequestWildcard,
        MqttTopics.StationClaimEndWildcard,
        MqttTopics.StationCreditAckWildcard,
        MqttTopics.StationAppInventoryWildcard,
        MqttTopics.CoinAcceptorStatusWildcard,
        MqttTopics.CoinAcceptorPulseWildcard,
        MqttTopics.StationCommandWildcard
    ];

    private readonly ServerMqttConfig _config;
    private readonly IMqttClient _client;
    private readonly ReconnectBackoff _backoff = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _reconnectLock = new();

    private MqttClientOptions? _options;
    private Task? _reconnectLoop;
    private volatile bool _isDisposing;

    public RentalServerMqttService(ServerMqttConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ConnectedAsync += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
    }

    public event EventHandler<MqttConnectionStatus>? StatusChanged;

    // Inbound routing — host assigns these after construction. Each delegate receives
    // (topic, payload, cancellationToken). A null delegate means "ignore this class
    // of inbound message" (useful in unit-style smoke tests).
    public Func<string, string, CancellationToken, Task>? OnStationStatus { get; set; }
    public Func<string, string, CancellationToken, Task>? OnClaimRequest { get; set; }
    public Func<string, string, CancellationToken, Task>? OnClaimEnd { get; set; }
    public Func<string, string, CancellationToken, Task>? OnSessionClear { get; set; }
    public Func<string, string, CancellationToken, Task>? OnCreditAck { get; set; }
    public Func<string, string, CancellationToken, Task>? OnStationAppInventory { get; set; }
    public Func<string, string, CancellationToken, Task>? OnAcceptorStatus { get; set; }
    public Func<string, string, CancellationToken, Task>? OnPulse { get; set; }
    public Func<string, string, CancellationToken, Task>? OnCommandAck { get; set; }

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);

        await ConnectOnceAsync(linked.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposing = true;
        _disposeCts.Cancel();

        Task? reconnectLoop;
        lock (_reconnectLock)
        {
            reconnectLoop = _reconnectLoop;
        }

        if (reconnectLoop is not null)
        {
            try
            {
                await reconnectLoop;
            }
            catch
            {
                // Best effort shutdown. The loop reports per-attempt failures
                // through StatusChanged while the app is alive.
            }
        }

        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync(MqttClientDisconnectOptionsReason.NormalDisconnection);
            }
            catch
            {
                // The process is already shutting down; avoid surfacing a broker
                // race as an app-close failure.
            }
        }

        _client.Dispose();
        _disposeCts.Dispose();
        _connectGate.Dispose();
    }

    public async Task PublishSessionAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            PublishStatus(false, $"Skipping session publish for {state.StationId} — broker not connected.");
            return;
        }

        var payload = new SessionMessage
        {
            SessionId = state.SessionId,
            StationId = state.StationId,
            DeviceType = state.DeviceType,
            Locked = state.IsLocked,
            RemainingSec = state.RemainingSecondsAtAnchor,
            LastCreditSeqNo = state.LastCreditSeqNo,
            UpdatedAt = state.UpdatedAt
        };

        await PublishJsonAsync(
            MqttTopics.StationSession(state.StationId),
            payload,
            retain: true,
            cancellationToken);
    }

    public Task PublishCreditAsync(
        string stationId,
        CreditMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.StationCredit(stationId),
            message,
            retain: false,
            cancellationToken);
    }

    public Task PublishClaimResultAsync(
        string stationId,
        ClaimResultMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.StationClaimResult(stationId),
            message,
            retain: false,
            cancellationToken);
    }

    public Task PublishPulseAckAsync(
        string acceptorId,
        long seqNo,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.CoinAcceptorPulseAck(acceptorId),
            new PulseAckMessage { SeqNo = seqNo },
            retain: false,
            cancellationToken);
    }

    public Task PublishLedAsync(
        string acceptorId,
        LedCommandMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.CoinAcceptorLed(acceptorId),
            message,
            retain: false,
            cancellationToken);
    }

    // S21a: retained publish of the current rate sheet. Stations subscribed to
    // `rental/config/rates` see the latest version immediately on connect.
    public Task PublishRatesAsync(
        RateConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.ConfigRates,
            message,
            retain: true,
            cancellationToken);
    }

    public Task PublishTabletAppsAsync(
        TabletAppConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.ConfigTabletApps,
            message,
            retain: true,
            cancellationToken);
    }

    public Task PublishTabletAdminAsync(
        TabletAdminConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.ConfigTabletAdmin,
            message,
            retain: true,
            cancellationToken);
    }

    public Task PublishAbusePreventionAsync(
        AbusePreventionConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.ConfigAbusePrevention,
            message,
            retain: true,
            cancellationToken);
    }

    public Task PublishPcPolicyAsync(
        PcPolicyConfigMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.ConfigPcPolicy,
            message,
            retain: true,
            cancellationToken);
    }

    public Task PublishCommandAsync(
        AdminCommandMessage message,
        CancellationToken cancellationToken = default)
    {
        return PublishJsonAsync(
            MqttTopics.StationCommand(message.StationId),
            message,
            retain: false,
            cancellationToken);
    }

    public async Task PublishRawAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        await _client.PublishAsync(message, cancellationToken);
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_client.IsConnected)
            {
                await PublishDevHelloAsync(cancellationToken);
                return;
            }

            _options ??= BuildOptions();

            PublishStatus(false, $"Connecting to {_config.Host}:{_config.Port}...");

            await _client.ConnectAsync(_options, cancellationToken);
            await SubscribeAsync(cancellationToken);
            await PublishDevHelloAsync(cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _backoff.Reset();
        PublishStatus(true, "Connected to local broker.");
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var detail = args.Exception?.Message ?? args.ReasonString;
        PublishStatus(false, "Disconnected from local broker.", detail);

        if (_isDisposing || _disposeCts.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        lock (_reconnectLock)
        {
            if (_reconnectLoop is null || _reconnectLoop.IsCompleted)
            {
                _reconnectLoop = Task.Run(() => RunReconnectLoopAsync(_disposeCts.Token));
            }
        }

        return Task.CompletedTask;
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_client.IsConnected)
        {
            var delay = _backoff.NextDelay();
            PublishStatus(
                false,
                $"Reconnecting in {delay.TotalSeconds:F0}s (attempt {_backoff.AttemptCount}).");

            try
            {
                await Task.Delay(delay, cancellationToken);
                await ConnectOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PublishStatus(false, "Reconnect attempt failed.", ex.Message);
            }
        }
    }

    private async Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payload = GetPayloadString(args.ApplicationMessage);

        try
        {
            // Order matters only for performance — topics are disjoint. Each handler
            // is invoked at most once per message.
            if (OnStationStatus is not null &&
                MqttTopics.TryGetStationIdFromStatusTopic(topic, out _))
            {
                await OnStationStatus(topic, payload, CancellationToken.None);
                return;
            }

            if (OnClaimRequest is not null &&
                MqttTopics.TryGetStationIdFromClaimRequestTopic(topic, out _))
            {
                await OnClaimRequest(topic, payload, CancellationToken.None);
                return;
            }

            if (OnClaimEnd is not null &&
                MqttTopics.TryGetStationIdFromClaimEndTopic(topic, out _))
            {
                await OnClaimEnd(topic, payload, CancellationToken.None);
                return;
            }

            if (OnSessionClear is not null &&
                MqttTopics.TryGetStationIdFromSessionClearTopic(topic, out _))
            {
                await OnSessionClear(topic, payload, CancellationToken.None);
                return;
            }

            if (OnCreditAck is not null &&
                MqttTopics.TryGetStationIdFromCreditAckTopic(topic, out _))
            {
                await OnCreditAck(topic, payload, CancellationToken.None);
                return;
            }

            if (OnStationAppInventory is not null &&
                MqttTopics.TryGetStationIdFromAppInventoryTopic(topic, out _))
            {
                await OnStationAppInventory(topic, payload, CancellationToken.None);
                return;
            }

            if (OnCommandAck is not null &&
                MqttTopics.TryGetStationIdFromCommandAckTopic(topic, out _))
            {
                await OnCommandAck(topic, payload, CancellationToken.None);
                return;
            }

            if (OnAcceptorStatus is not null &&
                MqttTopics.TryGetAcceptorIdFromStatusTopic(topic, out _))
            {
                await OnAcceptorStatus(topic, payload, CancellationToken.None);
                return;
            }

            if (OnPulse is not null &&
                MqttTopics.TryGetAcceptorIdFromPulseTopic(topic, out _))
            {
                await OnPulse(topic, payload, CancellationToken.None);
                return;
            }
        }
        catch (Exception ex)
        {
            PublishStatus(_client.IsConnected, $"Inbound handler failed for {topic}.", ex.Message);
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder();
        foreach (var topic in SubscribedTopics)
        {
            builder.WithTopicFilter(f => f
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        }

        await _client.SubscribeAsync(builder.Build(), cancellationToken);
        PublishStatus(true, "Subscribed to station + acceptor topics.");
    }

    private async Task PublishDevHelloAsync(CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.DevHello)
            .WithPayload("""{"app":"RentalServer","probe":"hello"}""")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
        PublishStatus(true, $"Published retained probe to {MqttTopics.DevHello}.");
    }

    private async Task PublishJsonAsync<T>(
        string topic,
        T payload,
        bool retain,
        CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            PublishStatus(false, $"Skipping publish to {topic} — broker not connected.");
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"rental-server-{Environment.MachineName}")
            .WithTcpServer(_config.Host, _config.Port)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        // Only authenticate when both fields are non-blank — blank means
        // "anonymous dev broker" (S2 Mosquitto default, no auth).
        if (_config.HasCredentials)
        {
            builder.WithCredentials(_config.Username, _config.Password);
        }

        return builder.Build();
    }

    private void PublishStatus(bool isConnected, string message, string? detail = null)
    {
        StatusChanged?.Invoke(this, new MqttConnectionStatus(isConnected, message, detail));
    }

    private static string GetPayloadString(MqttApplicationMessage message)
    {
        return Encoding.UTF8.GetString(message.Payload.ToArray());
    }
}
