using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Protocol;
using RentalShared.Updates;

namespace RentalServer;

/// <summary>
/// Bridges GitHub-hosted "online" updates onto the LOCAL broker. Every few minutes
/// it fetches update.json from the releases repo and, when the version changes,
/// republishes it (retained) to <see cref="UpdateManifest.MqttTopic"/> — so the
/// server's "Check for Updates" and the PC/tablet update badges all light up
/// through their existing local-broker path. This lets the operator publish a
/// release from anywhere (scripts/publish-online.ps1 + gh) without touching the
/// shop's IP or broker. Best-effort: any failure is logged and retried next tick;
/// it never throws into the host.
/// </summary>
public sealed class OnlineUpdatePoller : IDisposable
{
    // The releases repo's manifest (raw on `main`). Change if you fork the repo.
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/rjcantare/slotify-releases/main/update.json";
    private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly string _brokerHost;
    private readonly int _brokerPort;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private string? _lastRelayedVersion;

    public OnlineUpdatePoller(string brokerHost, int brokerPort, Action<string>? log = null)
    {
        _brokerHost = brokerHost;
        _brokerPort = brokerPort;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Slotify-Server-UpdatePoller");
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(FirstPollDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log?.Invoke($"[online-update] poll failed: {ex.Message}"); }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Fetch the manifest and, if its version is new, relay it to the broker.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var json = (await _http.GetStringAsync(ManifestUrl, ct)).TrimStart('﻿').Trim();

        string? version;
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            version = manifest?.Version;
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"[online-update] manifest unparseable: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(version)) return;
        if (version == "0.0.0") return;             // seed placeholder — nothing to ship
        if (version == _lastRelayedVersion) return; // already relayed this version

        // Publish the manifest text VERBATIM (not a re-serialized DTO) so the
        // hashes/URLs and any extra fields stay byte-faithful to what was signed
        // by the publish step.
        await PublishRetainedAsync(json, ct);
        _lastRelayedVersion = version;
        _log?.Invoke($"[online-update] relayed v{version} to {_brokerHost}:{_brokerPort}.");
    }

    private async Task PublishRetainedAsync(string json, CancellationToken ct)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_brokerHost, _brokerPort)
                .Build();
            await client.ConnectAsync(options, ct);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(UpdateManifest.MqttTopic)
                .WithPayload(json)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(true)
                .Build();
            await client.PublishAsync(message, ct);
            await client.DisconnectAsync();
        }
        finally
        {
            client.Dispose();
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* shutting down */ }
        _http.Dispose();
        _cts.Dispose();
    }
}
