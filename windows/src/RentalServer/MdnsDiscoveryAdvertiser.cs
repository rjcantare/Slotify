using System.Diagnostics;
using System.Globalization;
using System.Net;
using Makaretu.Dns;
using RentalShared.Discovery;

namespace RentalServer;

/// <summary>
/// Advertises the MQTT broker endpoint over mDNS / DNS-SD as
/// <c>_slotify-mqtt._tcp.local</c> so Wi-Fi devices (Android tablets, the
/// ESP32 coin box) can resolve the broker without a hardcoded IP.
///
/// Runs ALONGSIDE <see cref="UdpDiscoveryBroadcaster"/>: the UDP limited
/// broadcast serves wired PCs (APs don't forward 255.255.255.255 to Wi-Fi),
/// while this multicast advertisement reaches Wi-Fi (224.0.0.251 IS forwarded
/// by APs). Guardrail: carries only the broker host/port — the MQTT contract
/// is unchanged.
/// </summary>
public sealed class MdnsDiscoveryAdvertiser : IDisposable
{
    private readonly string _brokerHost;
    private readonly int _brokerPort;
    private readonly string _appVersion;
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;

    public MdnsDiscoveryAdvertiser(string brokerHost, int brokerPort, string appVersion)
    {
        _brokerHost = brokerHost;
        _brokerPort = brokerPort;
        _appVersion = appVersion;
    }

    public void Start()
    {
        if (_sd is not null)
        {
            return;
        }

        // Pin the published A record to the resolved LAN IP when brokerHost is
        // a literal IP (the common case — GetLanIpAddress returns a dotted
        // quad). This keeps Wi-Fi devices from resolving a VM/loopback address.
        // If brokerHost is a hostname, fall back to Makaretu's link-local set.
        IEnumerable<IPAddress>? addresses =
            IPAddress.TryParse(_brokerHost, out var ip) ? new[] { ip } : null;

        var profile = new ServiceProfile(
            MdnsDiscovery.InstanceName,
            MdnsDiscovery.ServiceType,
            (ushort)_brokerPort,
            addresses);

        // TXT records — parity with the UDP payload and an explicit endpoint
        // for resolvers that read TXT instead of following the SRV/A chain.
        profile.AddProperty(MdnsDiscovery.TxtBrokerHost, _brokerHost);
        profile.AddProperty(MdnsDiscovery.TxtBrokerPort, _brokerPort.ToString(CultureInfo.InvariantCulture));
        profile.AddProperty(MdnsDiscovery.TxtAppVersion, _appVersion);

        // Answer on ALL interfaces (standard mDNS, like Bonjour/Avahi). Which
        // IP devices actually connect to is fixed by the pinned A record
        // (addresses above) + a deterministic advertiseHost — NOT by binding
        // the responder to one NIC. An earlier revision filtered the
        // MulticastService down to only the NIC owning the advertised IP; on a
        // multi-homed host that caused a total mDNS blackout whenever the chosen
        // IP landed on the wrong NIC (or matched no local NIC), so Wi-Fi devices
        // (tablet, ESP32) got "unable to resolve host" instead of an answer.
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);
        _sd.Advertise(profile);

        Debug.WriteLine(
            $"[discovery] mDNS advertiser started — {MdnsDiscovery.ServiceType} " +
            $"-> {_brokerHost}:{_brokerPort}");
    }

    public void Dispose()
    {
        // Disposes ServiceDiscovery first (it holds a reference to _mdns and
        // unhooks its event handlers), then the MulticastService we own.
        _sd?.Dispose();
        _sd = null;
        _mdns?.Dispose();
        _mdns = null;
    }
}
