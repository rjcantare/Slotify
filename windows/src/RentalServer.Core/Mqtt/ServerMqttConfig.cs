namespace RentalServer.Core.Mqtt;

/// <summary>
/// MQTT broker connection settings for the server. Passed to
/// <see cref="RentalServerMqttService"/> at construction so the Core
/// library stays free of JSON/config-file loading.
/// </summary>
public sealed record ServerMqttConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 1883;

    /// <summary>
    /// Optional MQTT broker username. Leave null/blank for anonymous dev broker.
    /// When set, <see cref="Password"/> must also be set.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional MQTT broker password. Leave null/blank for anonymous dev broker.
    /// When set, <see cref="Username"/> must also be set.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional override for the LAN IP/host advertised to discovery clients
    /// (UDP broadcast + mDNS). Leave null/blank to auto-detect via
    /// <c>GetLanIpAddress()</c>, which is the right choice on a single-NIC
    /// host. Set this explicitly on a multi-homed server (e.g. a second NIC)
    /// where auto-detection could pick either adapter.
    /// </summary>
    public string? AdvertiseHost { get; init; }

    /// <summary>
    /// Returns true when both username and password are non-blank,
    /// i.e. the service should authenticate with the broker.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password);
}
