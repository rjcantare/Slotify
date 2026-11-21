using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using RentalClient.Windows.Configuration;
using RentalClient.Windows.Discovery;
using RentalShared.Mqtt;
using RentalShared.Updates;

namespace RentalClient.Windows.Mqtt;

// Owns the MQTT connection for a single station. On ConnectAsync:
//   - registers an LWT on `rental/station/{stationId}/status` (retained, online=false)
//   - publishes the online retained status overwriting the LWT slot
// On DisconnectGracefullyAsync (or DisposeAsync):
//   - publishes the offline retained status explicitly — MQTT graceful DISCONNECT
//     discards the LWT, so this manual publish is the substitute (per the
//     "Connect Sequence" rule in the MQTT Protocol section of CLAUDE.md)
//   - then issues a normal DISCONNECT
//
// Reconnect attempts are orchestrated by MainWindow's timer so the visible UI
// can keep showing whether the client is online, autonomous, or untrusted.
public sealed class StationMqttClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ClientConfig _config;
    private readonly IMqttClient _client;
    private long _bootTimestamp;

    // S46: resolved broker endpoint — may differ from _config.Broker.Host
    // when the config has "auto" and UDP discovery found the server.
    private string _brokerHost = string.Empty;
    private int _brokerPort;

    public StationMqttClient(ClientConfig config)
    {
        _config = config;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ConnectedAsync += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
    }

    public event EventHandler<MqttConnectionStatus>? StatusChanged;

    // Fires when a session message is received on the station's session topic.
    // Subscribers receive a deserialized SessionMessage; parse failures are logged
    // via StatusChanged and do not fire this event.
    public event EventHandler<SessionMessage>? SessionReceived;

    // Fires when a claim/result message is received on the station's claim/result
    // topic. Same parse-failure semantics as SessionReceived.
    public event EventHandler<ClaimResultMessage>? ClaimResultReceived;

    // Fires when a credit message is received on the station's credit topic.
    // Same parse-failure semantics as SessionReceived.
    public event EventHandler<CreditMessage>? CreditReceived;

    public event EventHandler<RateConfigMessage>? RateConfigReceived;

    public event EventHandler<AbusePreventionConfigMessage>? AbusePreventionConfigReceived;

    public event EventHandler<TabletAdminConfigMessage>? TabletAdminConfigReceived;

    public event EventHandler<PcPolicyConfigMessage>? PcPolicyConfigReceived;

    public event EventHandler<UpdateManifest>? UpdateManifestReceived;

    // S49: server-issued admin commands (restart_client / reboot_pc / shutdown_pc).
    // The handler is wired in MainWindow and is responsible for publishing the
    // ack back to the server before executing.
    public event EventHandler<AdminCommandMessage>? AdminCommandReceived;

    public bool IsConnected => _client.IsConnected;

    public string StationId => _config.StationId;

    public string BrokerEndpoint => $"{_brokerHost}:{_brokerPort}";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client.IsConnected)
        {
            await PublishStationStatusAsync(online: true, cancellationToken);
            return;
        }

        // Resolve broker endpoint — "auto" triggers LAN discovery.
        _brokerHost = _config.Broker.Host;
        _brokerPort = _config.Broker.Port;

        if (IsAutoDiscovery(_brokerHost))
        {
            // mDNS first (reaches Wi-Fi and wired); UDP broadcast as a wired-LAN
            // backstop. Same broker resolution the tablets/ESP32 use.
            var discovered = await MdnsDiscoveryListener.DiscoverAsync()
                             ?? await UdpDiscoveryListener.DiscoverAsync();
            if (discovered is not null)
            {
                _brokerHost = discovered.BrokerHost;
                _brokerPort = discovered.BrokerPort;
                EmitStatus(false, $"Discovered broker at {_brokerHost}:{_brokerPort}.");
            }
            else
            {
                EmitStatus(false, "LAN discovery found no server. Will retry with configured host.");
            }
        }

        _bootTimestamp = Stopwatch.GetTimestamp();

        var lwtPayload = SerializeStatus(online: false, includeBootFields: false);

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId($"rental-station-{_config.StationId}")
            .WithTcpServer(_brokerHost, _brokerPort)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithWillTopic(MqttTopics.StationStatus(_config.StationId))
            .WithWillPayload(lwtPayload)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true);

        // Only authenticate when both fields are non-blank — blank means
        // "anonymous dev broker" (S2 Mosquitto default, no auth).
        if (_config.Broker.HasCredentials)
        {
            optionsBuilder.WithCredentials(_config.Broker.Username, _config.Broker.Password);
        }

        var options = optionsBuilder.Build();

        EmitStatus(false, $"Connecting to {BrokerEndpoint} as {_config.StationId}...");

        await _client.ConnectAsync(options, cancellationToken);
        await PublishStationStatusAsync(online: true, cancellationToken);
        await SubscribeAsync(cancellationToken);
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        // Station-scoped subscriptions plus retained rate config for display.
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(MqttTopics.StationSession(_config.StationId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.StationClaimResult(_config.StationId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.StationCredit(_config.StationId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.ConfigRates)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.ConfigAbusePrevention)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.ConfigTabletAdmin)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.ConfigPcPolicy)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(UpdateManifest.MqttTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .WithTopicFilter(f => f.WithTopic(MqttTopics.StationCommand(_config.StationId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        await _client.SubscribeAsync(subscribeOptions, cancellationToken);
        EmitStatus(true, $"Subscribed to session + claim/result + credit + rates + admin + updates + commands for {_config.StationId}.");
    }

    public async Task PublishClaimRequestAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Not connected to broker — cannot publish claim request.");
        }

        var payload = new ClaimRequestMessage
        {
            RequestId = requestId,
            StationId = _config.StationId,
            RequestedAt = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture)
        };

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.StationClaimRequest(_config.StationId))
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task PublishClaimEndAsync(
        string requestId,
        string claimId,
        CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Not connected to broker — cannot publish claim end.");
        }

        var payload = new ClaimEndMessage
        {
            RequestId = requestId,
            ClaimId = claimId,
            StationId = _config.StationId,
            EndedAt = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture)
        };

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.StationClaimEnd(_config.StationId))
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task PublishSessionClearAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Not connected to broker — cannot publish session clear request.");
        }

        var payload = new SessionClearRequestMessage
        {
            RequestId = requestId,
            StationId = _config.StationId,
            RequestedAt = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture)
        };

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.StationSessionClear(_config.StationId))
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    // S49: ack a server-issued admin command before executing it. Result is
    // "acknowledged" (handler accepted), "rejected" (unknown command / wrong
    // station), or "completed" (rare — used only when execution is synchronous
    // and finishes before the process dies). For restart/reboot/shutdown the
    // ack races the OS teardown, so the server treats absence-of-ack as the
    // expected case for those commands.
    public async Task PublishCommandAckAsync(
        string requestId,
        string result,
        string detail,
        CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Not connected to broker — cannot publish command ack.");
        }

        var payload = new AdminCommandAckMessage
        {
            RequestId = requestId,
            StationId = _config.StationId,
            Result = result,
            Detail = detail
        };

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.StationCommandAck(_config.StationId))
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task PublishCreditAckAsync(long seqNo, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Not connected to broker — cannot publish credit ack.");
        }

        var payload = new CreditAckMessage
        {
            SeqNo = seqNo,
            StationId = _config.StationId
        };

        var json = JsonSerializer.Serialize(payload);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.StationCreditAck(_config.StationId))
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task DisconnectGracefullyAsync(CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        try
        {
            await PublishStationStatusAsync(online: false, cancellationToken);
        }
        catch (Exception ex)
        {
            EmitStatus(_client.IsConnected, "Could not publish offline status before disconnect.", ex.Message);
        }

        await _client.DisconnectAsync(
            MqttClientDisconnectOptionsReason.NormalDisconnection,
            cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectGracefullyAsync();
        }
        catch
        {
            // Best-effort during dispose.
        }
        finally
        {
            _client.Dispose();
        }
    }

    private async Task PublishStationStatusAsync(bool online, CancellationToken cancellationToken)
    {
        var payload = SerializeStatus(online, includeBootFields: online);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.StationStatus(_config.StationId))
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    private string SerializeStatus(bool online, bool includeBootFields)
    {
        var dto = new StationStatusMessage
        {
            Online = online,
            DeviceId = _config.StationId,
            DeviceType = includeBootFields ? _config.DeviceType : null,
            AppVersion = includeBootFields ? _config.AppVersion : null,
            UptimeSec = includeBootFields ? GetUptimeSeconds() : null
        };

        return JsonSerializer.Serialize(dto, PayloadJsonOptions);
    }

    private long GetUptimeSeconds()
    {
        if (_bootTimestamp == 0)
        {
            return 0;
        }

        var elapsed = Stopwatch.GetTimestamp() - _bootTimestamp;
        return (long)(elapsed / (double)Stopwatch.Frequency);
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        EmitStatus(true, $"Connected as {_config.StationId} to {BrokerEndpoint}.");
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var detail = args.Exception?.Message ?? args.ReasonString;
        EmitStatus(false, "Disconnected from broker.", detail);
        return Task.CompletedTask;
    }

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var stationId = _config.StationId;

        if (topic == MqttTopics.StationSession(stationId))
        {
            DispatchPayload<SessionMessage>(args, m => SessionReceived?.Invoke(this, m), "session");
        }
        else if (topic == MqttTopics.StationClaimResult(stationId))
        {
            DispatchPayload<ClaimResultMessage>(args, m => ClaimResultReceived?.Invoke(this, m), "claim/result");
        }
        else if (topic == MqttTopics.StationCredit(stationId))
        {
            DispatchPayload<CreditMessage>(args, m => CreditReceived?.Invoke(this, m), "credit");
        }
        else if (topic == MqttTopics.ConfigRates)
        {
            DispatchPayload<RateConfigMessage>(args, m => RateConfigReceived?.Invoke(this, m), "rates");
        }
        else if (topic == MqttTopics.ConfigAbusePrevention)
        {
            DispatchPayload<AbusePreventionConfigMessage>(args, m => AbusePreventionConfigReceived?.Invoke(this, m), "abuse-prevention");
        }
        else if (topic == MqttTopics.ConfigTabletAdmin)
        {
            DispatchPayload<TabletAdminConfigMessage>(args, m => TabletAdminConfigReceived?.Invoke(this, m), "tablet-admin");
        }
        else if (topic == MqttTopics.ConfigPcPolicy)
        {
            DispatchPayload<PcPolicyConfigMessage>(args, m => PcPolicyConfigReceived?.Invoke(this, m), "pc-policy");
        }
        else if (topic == UpdateManifest.MqttTopic)
        {
            DispatchPayload<UpdateManifest>(args, m => UpdateManifestReceived?.Invoke(this, m), "update-manifest");
        }
        else if (topic == MqttTopics.StationCommand(stationId))
        {
            DispatchPayload<AdminCommandMessage>(args, m => AdminCommandReceived?.Invoke(this, m), "command");
        }

        return Task.CompletedTask;
    }

    private void DispatchPayload<T>(
        MqttApplicationMessageReceivedEventArgs args,
        Action<T> emit,
        string topicLabel)
        where T : class
    {
        try
        {
            var raw = args.ApplicationMessage.Payload.ToArray();
            var payload = Encoding.UTF8.GetString(raw);

            // PowerShell pipelines (and some other Windows tooling) prepend a
            // UTF-8 BOM to stdin-fed messages. System.Text.Json rejects U+FEFF
            // as an invalid token, so strip it before deserialization.
            if (payload.Length > 0 && payload[0] == '\uFEFF')
            {
                payload = payload[1..];
            }

            var message = JsonSerializer.Deserialize<T>(
                payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (message is not null)
            {
                emit(message);
            }
        }
        catch (Exception ex)
        {
            EmitStatus(_client.IsConnected, $"{topicLabel} message parse failed.", ex.Message);
        }
    }

    private void EmitStatus(bool connected, string message, string? detail = null)
    {
        StatusChanged?.Invoke(this, new MqttConnectionStatus(connected, message, detail));
    }

    private static bool IsAutoDiscovery(string host)
    {
        var trimmed = host?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ||
               string.Equals(trimmed, ClientConfig.AutoBrokerHostSentinel, StringComparison.OrdinalIgnoreCase);
    }
}
