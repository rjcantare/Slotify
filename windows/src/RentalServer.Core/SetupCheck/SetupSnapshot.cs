namespace RentalServer.Core.SetupCheck;

// Facts the server gathers for "Check setup". The WPF layer fills this in
// (Windows services, firewall, network adapters, database); SetupChecker turns
// it into pass/warn/fail results. Keeping the facts plain makes every check
// testable without a real broker, firewall or network.

public enum ServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    Running
}

public enum NetworkProfile
{
    Unknown,
    Domain,
    Private,
    Public
}

/// <param name="Readable">False when the firewall could not be queried at all.</param>
/// <param name="FirewallOn">True when Windows Firewall is on for the active profile.</param>
/// <param name="MqttRuleOk">Slotify's inbound TCP 1883 rule exists, is enabled and covers the active profile.</param>
/// <param name="MdnsRuleOk">Slotify's inbound UDP 5353 (mDNS) rule exists and is enabled.</param>
public sealed record FirewallInfo(
    bool Readable,
    bool? FirewallOn,
    bool? MqttRuleOk,
    bool? MdnsRuleOk);

/// <param name="Name">Adapter name as Windows shows it, e.g. "Ethernet" or "Wi-Fi".</param>
public sealed record AdapterInfo(
    string Name,
    string Ipv4,
    bool HasGateway,
    bool LooksVirtual);

/// <param name="DeviceType">"pc", "tab" or "phn".</param>
public sealed record UnitInfo(
    string StationId,
    string DeviceType,
    bool Online,
    DateTimeOffset? LastSeen,
    string? AppVersion);

public sealed record CoinBoxInfo(
    string AcceptorId,
    bool Online,
    DateTimeOffset? LastSeen,
    string? FirmwareVersion);

public sealed record SetupSnapshot
{
    public string ServerVersion { get; init; } = "?";
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public ServiceState Mosquitto { get; init; } = ServiceState.Unknown;
    public int MqttPort { get; init; } = 1883;

    /// <summary>Local addresses with a TCP listener on <see cref="MqttPort"/>, e.g. "0.0.0.0", "127.0.0.1".</summary>
    public IReadOnlyList<string> MqttListenAddresses { get; init; } = Array.Empty<string>();

    /// <summary>The server's own MQTT client is connected to the broker.</summary>
    public bool ServerConnectedToBroker { get; init; }

    public NetworkProfile Profile { get; init; } = NetworkProfile.Unknown;
    public FirewallInfo Firewall { get; init; } = new(false, null, null, null);

    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = Array.Empty<AdapterInfo>();

    /// <summary>The LAN IP the server advertises for discovery (null if discovery never started).</summary>
    public string? AdvertisedIp { get; init; }
    public bool AdvertiseHostOverridden { get; init; }

    public bool UdpDiscoveryRunning { get; init; }
    public bool MdnsRunning { get; init; }

    public IReadOnlyList<UnitInfo> Units { get; init; } = Array.Empty<UnitInfo>();
    public IReadOnlyList<CoinBoxInfo> CoinBoxes { get; init; } = Array.Empty<CoinBoxInfo>();

    /// <summary>True when the PC/tablet admin password is still the public default; null if unknown.</summary>
    public bool? DefaultAdminPassword { get; init; }
}
