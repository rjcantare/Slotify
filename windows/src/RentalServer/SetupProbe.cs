using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using RentalServer.Core.Acceptors;
using RentalServer.Core.SetupCheck;
using RentalServer.Core.Stations;

namespace RentalServer;

/// <summary>
/// Gathers the Windows-side facts for "Check setup": the Mosquitto service,
/// TCP listeners, Windows Firewall and network adapters. Every probe is
/// best-effort: a probe that fails reports "unknown" instead of throwing,
/// so one locked-down setting never hides the rest of the report.
/// </summary>
public static class SetupProbe
{
    public sealed record ServerState(
        string ServerVersion,
        int MqttPort,
        bool ServerConnectedToBroker,
        string? AdvertisedIp,
        bool AdvertiseHostOverridden,
        bool UdpDiscoveryRunning,
        bool MdnsRunning,
        IReadOnlyList<StationRecord> Stations,
        IReadOnlyList<AcceptorRecord> Acceptors,
        bool? DefaultAdminPassword);

    // Names the server installer gives its firewall rules (windows/installer/server.iss).
    private const string MqttRuleName = "RentalSystem Broker (MQTT 1883)";
    private const string MdnsRuleName = "RentalSystem mDNS (UDP 5353)";

    private static readonly string[] VirtualAdapterMarkers =
    {
        "Hyper-V", "vEthernet", "VMware", "VirtualBox", "Loopback", "VPN", "TAP-", "WireGuard",
        "ZeroTier", "Tailscale", "Hamachi", "Radmin", "OpenVPN", "Npcap", "WSL"
    };

    public static SetupSnapshot Gather(ServerState state)
    {
        var (profile, firewall) = ProbeFirewall();

        return new SetupSnapshot
        {
            ServerVersion = state.ServerVersion,
            Now = DateTimeOffset.Now,
            Mosquitto = ProbeMosquittoService(),
            MqttPort = state.MqttPort,
            MqttListenAddresses = ProbeListeners(state.MqttPort),
            ServerConnectedToBroker = state.ServerConnectedToBroker,
            Profile = profile,
            Firewall = firewall,
            Adapters = ProbeAdapters(),
            AdvertisedIp = state.AdvertisedIp,
            AdvertiseHostOverridden = state.AdvertiseHostOverridden,
            UdpDiscoveryRunning = state.UdpDiscoveryRunning,
            MdnsRunning = state.MdnsRunning,
            Units = state.Stations
                .Select(st => new UnitInfo(
                    st.StationId,
                    st.DeviceType,
                    st.IsOnline,
                    ParseTime(st.LastSeenAt),
                    st.AppVersion))
                .ToList(),
            CoinBoxes = state.Acceptors
                .Select(a => new CoinBoxInfo(a.AcceptorId, a.IsOnline, ParseTime(a.LastSeenAt), a.FirmwareVersion))
                .ToList(),
            DefaultAdminPassword = state.DefaultAdminPassword
        };
    }

    // ---- Mosquitto service --------------------------------------------------

    private static ServiceState ProbeMosquittoService()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = "query mosquitto",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return ServiceState.Unknown;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                return ServiceState.Unknown;
            }

            // 1060 = ERROR_SERVICE_DOES_NOT_EXIST
            if (process.ExitCode == 1060 || output.Contains("1060", StringComparison.Ordinal))
            {
                return ServiceState.NotInstalled;
            }

            // State names (RUNNING, STOPPED, ...) are not localized by sc.exe.
            if (output.Contains("RUNNING", StringComparison.Ordinal))
            {
                return ServiceState.Running;
            }

            return output.Contains("STOPPED", StringComparison.Ordinal) ||
                   output.Contains("PENDING", StringComparison.Ordinal)
                ? ServiceState.Stopped
                : ServiceState.Unknown;
        }
        catch
        {
            return ServiceState.Unknown;
        }
    }

    // ---- TCP listeners --------------------------------------------------------

    private static IReadOnlyList<string> ProbeListeners(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(ep => ep.Port == port)
                .Select(ep => ep.Address.Equals(IPAddress.IPv6Any) ? "::" : ep.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ---- Windows Firewall (HNetCfg.FwPolicy2, late-bound COM) -----------------

    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;

    private static (NetworkProfile Profile, FirewallInfo Firewall) ProbeFirewall()
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null)
            {
                return (NetworkProfile.Unknown, new FirewallInfo(false, null, null, null));
            }

            policy = Activator.CreateInstance(type)!;
            var current = Convert.ToInt32(Get(policy, "CurrentProfileTypes"), CultureInfo.InvariantCulture);

            var profile =
                (current & ProfilePublic) != 0 ? NetworkProfile.Public :
                (current & ProfilePrivate) != 0 ? NetworkProfile.Private :
                (current & ProfileDomain) != 0 ? NetworkProfile.Domain :
                NetworkProfile.Unknown;

            var firewallOn = false;
            foreach (var p in new[] { ProfileDomain, ProfilePrivate, ProfilePublic })
            {
                if ((current & p) != 0 && Convert.ToBoolean(Get(policy, "FirewallEnabled", p), CultureInfo.InvariantCulture))
                {
                    firewallOn = true;
                }
            }

            var rules = Get(policy, "Rules")!;
            try
            {
                return (profile, new FirewallInfo(
                    Readable: true,
                    FirewallOn: firewallOn,
                    MqttRuleOk: RuleAllows(rules, MqttRuleName, current),
                    MdnsRuleOk: RuleAllows(rules, MdnsRuleName, current)));
            }
            finally
            {
                Release(rules);
            }
        }
        catch
        {
            return (NetworkProfile.Unknown, new FirewallInfo(false, null, null, null));
        }
        finally
        {
            Release(policy);
        }
    }

    private static bool RuleAllows(object rules, string name, int currentProfiles)
    {
        object? rule = null;
        try
        {
            rule = Call(rules, "Item", name); // throws when no rule has this name
            var enabled = Convert.ToBoolean(Get(rule!, "Enabled"), CultureInfo.InvariantCulture);
            var action = Convert.ToInt32(Get(rule!, "Action"), CultureInfo.InvariantCulture);       // 1 = allow
            var direction = Convert.ToInt32(Get(rule!, "Direction"), CultureInfo.InvariantCulture); // 1 = inbound
            var profiles = Convert.ToInt32(Get(rule!, "Profiles"), CultureInfo.InvariantCulture);
            return enabled && action == 1 && direction == 1 && (profiles & currentProfiles) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(rule);
        }
    }

    private static object? Get(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args, CultureInfo.InvariantCulture);

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    // ---- Network adapters ---------------------------------------------------------

    private static IReadOnlyList<AdapterInfo> ProbeAdapters()
    {
        var list = new List<AdapterInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var looksVirtual = VirtualAdapterMarkers.Any(m =>
                    ni.Name.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains(m, StringComparison.OrdinalIgnoreCase));

                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        list.Add(new AdapterInfo(ni.Name, ip.Address.ToString(), hasGateway, looksVirtual));
                    }
                }
            }
        }
        catch
        {
            // Report what we have.
        }

        return list;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime()
            : null;
}
