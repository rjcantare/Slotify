namespace RentalShared.Discovery;

/// <summary>
/// Shared constants for the mDNS / DNS-SD broker-discovery service.
/// Multicast (224.0.0.251) IS forwarded by Wi-Fi APs — unlike the limited
/// UDP broadcast used by <see cref="RentalDiscoveryMessage"/>, which routers
/// drop between the wired server and Wi-Fi — so this is the path Wi-Fi devices
/// (Android tablets, the ESP32 coin box) use to find the broker. It's the same
/// mechanism Chromecast/printers are found by.
///
/// Guardrail: discovery resolves ONLY the broker endpoint. The MQTT protocol,
/// schemas, QoS, retain, ACK, and dedup contract are unchanged
/// (per ProjectContext/ArchitechtureShift.md).
///
/// The Android (NsdManager) and ESP32 (ESPmDNS) clients live in sibling repos
/// and mirror these literals by hand — keep them in sync.
/// </summary>
public static class MdnsDiscovery
{
    /// <summary>
    /// DNS-SD service type. The service label "slotify-mqtt" is 12 chars,
    /// within the 1–15 limit of RFC 6763 §7.
    /// </summary>
    public const string ServiceType = "_slotify-mqtt._tcp";

    /// <summary>Instance label advertised under <see cref="ServiceType"/> (one server per shop).</summary>
    public const string InstanceName = "slotify-server";

    // TXT-record keys carried alongside the SRV/A records. Resolvers can read
    // these directly instead of following the SRV → A chain; values mirror the
    // UDP RentalDiscoveryMessage fields.
    public const string TxtBrokerHost = "brokerHost";
    public const string TxtBrokerPort = "brokerPort";
    public const string TxtAppVersion = "appVersion";
}
