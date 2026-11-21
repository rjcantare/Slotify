using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentalClient.Windows.Configuration;

public sealed record ClientConfig
{
    [JsonPropertyName("stationId")]
    public string StationId { get; init; } = string.Empty;

    /// <summary>
    /// Sentinel value in appsettings.json that opts into hostname-derived
    /// station ID. Recognized case-insensitively. Empty string is treated
    /// the same way so the default appsettings is usable on shared images.
    /// </summary>
    public const string AutoStationIdSentinel = "auto";

    /// <summary>
    /// Sentinel value in appsettings.json that opts into UDP LAN discovery
    /// for the broker host/port. Used in the shared iCafe8 image so clients
    /// auto-find the server without a hardcoded IP.
    /// </summary>
    public const string AutoBrokerHostSentinel = "auto";

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; init; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; init; } = string.Empty;

    [JsonPropertyName("broker")]
    public BrokerConfig Broker { get; init; } = new();

    [JsonPropertyName("debug")]
    public DebugConfig Debug { get; init; } = new();

    [JsonPropertyName("connection")]
    public ConnectionConfig Connection { get; init; } = new();

    [JsonPropertyName("countdownWarning")]
    public CountdownWarningConfig CountdownWarning { get; init; } = new();

    public static ClientConfig LoadFromDisk()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Client config not found at {path}. Expected appsettings.json next to the executable.");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ClientConfig>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config is null)
        {
            throw new InvalidDataException($"Failed to parse client config at {path}.");
        }

        // S30 deployment: a single shared image is the iCafe8 reality. The
        // committed appsettings.json sets stationId="auto" (or leaves it
        // empty) to opt into hostname-derived resolution so all 12 PCs
        // read the same JSON yet pick up unique station IDs from their
        // iCafe8-assigned hostnames. See deployment-procedure.md.
        config = config.ResolveStationIdFromHostname();

        config.Validate();
        return config;
    }

    /// <summary>
    /// Returns a copy of this config with StationId resolved to
    /// <c>Environment.MachineName</c> (lowercased) when the JSON value was
    /// <c>"auto"</c> (case-insensitive) or empty. Idempotent for non-sentinel
    /// values so a per-machine override remains untouched.
    /// </summary>
    public ClientConfig ResolveStationIdFromHostname()
    {
        var trimmed = StationId?.Trim() ?? string.Empty;
        var needsResolve =
            trimmed.Length == 0 ||
            string.Equals(trimmed, AutoStationIdSentinel, StringComparison.OrdinalIgnoreCase);
        if (!needsResolve)
        {
            return this;
        }

        var hostname = Environment.MachineName ?? string.Empty;
        return this with { StationId = hostname.Trim().ToLowerInvariant() };
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(StationId))
        {
            throw new InvalidDataException(
                "Client config: stationId is required. Set it explicitly in appsettings.json " +
                "or use \"auto\" to derive from Environment.MachineName (and confirm the OS " +
                "hostname is set to the desired station ID, e.g. pc-01).");
        }

        ValidateCore();
    }

    /// <summary>
    /// Validates broker, device type, connection, and countdown fields without
    /// requiring stationId — used by the provisioning wizard before the operator
    /// has chosen a station ID.
    /// </summary>
    public void ValidatePartial()
    {
        ValidateCore();
    }

    private void ValidateCore()
    {
        // Mirrors the locked enum in the MQTT protocol + DB schema.
        if (DeviceType is not ("pc" or "phn" or "tab"))
        {
            throw new InvalidDataException(
                $"Client config: deviceType must be 'pc', 'phn', or 'tab'; got '{DeviceType}'.");
        }

        if (string.IsNullOrWhiteSpace(Broker.Host))
        {
            throw new InvalidDataException("Client config: broker.host is required.");
        }

        if (Broker.Port <= 0 || Broker.Port > 65535)
        {
            throw new InvalidDataException(
                $"Client config: broker.port must be 1..65535; got {Broker.Port}.");
        }

        if (Connection.ReconnectIntervalSec <= 0)
        {
            throw new InvalidDataException(
                $"Client config: connection.reconnectIntervalSec must be > 0; got {Connection.ReconnectIntervalSec}.");
        }

        if (Connection.OfflineUntrustedAfterSec <= 0)
        {
            throw new InvalidDataException(
                $"Client config: connection.offlineUntrustedAfterSec must be > 0; got {Connection.OfflineUntrustedAfterSec}.");
        }

        if (CountdownWarning.FirstWarningSec <= 0)
        {
            throw new InvalidDataException(
                $"Client config: countdownWarning.firstWarningSec must be > 0; got {CountdownWarning.FirstWarningSec}.");
        }

        if (CountdownWarning.FinalCountdownStartSec <= 0)
        {
            throw new InvalidDataException(
                $"Client config: countdownWarning.finalCountdownStartSec must be > 0; got {CountdownWarning.FinalCountdownStartSec}.");
        }

        if (CountdownWarning.FirstWarningSec <= CountdownWarning.FinalCountdownStartSec)
        {
            throw new InvalidDataException(
                "Client config: countdownWarning.firstWarningSec must be greater than countdownWarning.finalCountdownStartSec.");
        }
    }

    public void SaveToDisk(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(path, json);
    }
}

public sealed record BrokerConfig
{
    [JsonPropertyName("host")]
    public string Host { get; init; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; init; } = 1883;

    /// <summary>
    /// Optional MQTT broker username. Leave blank for anonymous dev broker.
    /// When set, password must also be set (the pair travels together).
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>
    /// Optional MQTT broker password. Leave blank for anonymous dev broker.
    /// When set, username must also be set.
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary>
    /// Returns true when both username and password are non-blank,
    /// i.e. the client should authenticate with the broker.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);
}

public sealed record DebugConfig
{
    [JsonPropertyName("disableLockOverlay")]
    public bool DisableLockOverlay { get; init; }
}

public sealed record ConnectionConfig
{
    [JsonPropertyName("reconnectIntervalSec")]
    public int ReconnectIntervalSec { get; init; } = 5;

    [JsonPropertyName("offlineUntrustedAfterSec")]
    public int OfflineUntrustedAfterSec { get; init; } = 120;
}

public sealed record CountdownWarningConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("firstWarningSec")]
    public int FirstWarningSec { get; init; } = 60;

    [JsonPropertyName("finalCountdownStartSec")]
    public int FinalCountdownStartSec { get; init; } = 10;
}
