using System.Globalization;
using System.Text;

namespace RentalServer.Core.SetupCheck;

public enum CheckStatus
{
    Pass,
    Info,
    Warn,
    Fail
}

/// <param name="Area">Group shown in the UI, e.g. "MQTT broker".</param>
/// <param name="Detail">What was found. May name stations (UI only).</param>
/// <param name="Fix">One-line fix in simple Taglish; empty when nothing to do.</param>
/// <param name="ReportDetail">Detail for "Copy report": counts only, no station names. Null = use <paramref name="Detail"/>.</param>
public sealed record SetupCheckResult(
    string Area,
    string Title,
    CheckStatus Status,
    string Detail,
    string Fix = "",
    string? ReportDetail = null);

/// <summary>
/// Turns a <see cref="SetupSnapshot"/> into pass/warn/fail results for the
/// server's "Check setup" window. Pure: no I/O, so every rule is unit-tested.
/// </summary>
public static class SetupChecker
{
    public static IReadOnlyList<SetupCheckResult> Evaluate(SetupSnapshot s)
    {
        var results = new List<SetupCheckResult>();
        CheckBroker(s, results);
        CheckFirewall(s, results);
        CheckNetwork(s, results);
        CheckDiscovery(s, results);
        CheckUnits(s, results);
        CheckCoinBox(s, results);
        CheckSecurity(s, results);
        return results;
    }

    // ---- 1. MQTT broker ----------------------------------------------------

    private static void CheckBroker(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "MQTT broker";

        r.Add(s.Mosquitto switch
        {
            ServiceState.Running => new(area, "Mosquitto service", CheckStatus.Pass, "Running."),
            ServiceState.Stopped => new(area, "Mosquitto service", CheckStatus.Fail,
                "Installed but STOPPED. No PC, tablet or coin box can connect.",
                "Buksan ang Services (Win+R → services.msc) → \"Mosquitto Broker\" → Start, at gawing Automatic. O i-restart ang server PC."),
            ServiceState.NotInstalled => new(area, "Mosquitto service", CheckStatus.Fail,
                "Not installed. Slotify needs it to talk to the PCs, tablets and coin box.",
                "I-reinstall ang Slotify Server (RentalSystem-Server-Setup.exe). Kasama na doon ang Mosquitto."),
            _ => new(area, "Mosquitto service", CheckStatus.Warn,
                "Couldn't read the Windows service status.",
                "Tingnan sa Services (services.msc) kung Running ang \"Mosquitto Broker\".")
        });

        var listeners = s.MqttListenAddresses;
        var port = s.MqttPort;
        if (listeners.Count == 0)
        {
            r.Add(new(area, $"Port {port} listening", CheckStatus.Fail,
                $"Nothing is listening on TCP {port}.",
                "I-start ang Mosquitto (tingnan sa taas). Kung Running na, i-reinstall ang Slotify Server para maibalik ang mosquitto.conf."));
        }
        else if (listeners.All(IsLoopback))
        {
            r.Add(new(area, $"Port {port} listening", CheckStatus.Fail,
                $"Mosquitto only listens on this PC ({string.Join(", ", listeners)}:{port}). Other PCs can't reach it.",
                "Ang mosquitto.conf ay dapat may \"listener 1883 0.0.0.0\" at \"allow_anonymous true\". I-reinstall ang Slotify Server para maayos ito, tapos i-restart ang Mosquitto."));
        }
        else
        {
            r.Add(new(area, $"Port {port} listening", CheckStatus.Pass,
                $"Listening on {string.Join(", ", listeners.Where(a => !IsLoopback(a)))}:{port} (reachable from the LAN)."));
        }

        r.Add(s.ServerConnectedToBroker
            ? new(area, "Server connected to broker", CheckStatus.Pass, "Connected.")
            : new(area, "Server connected to broker", CheckStatus.Fail,
                "The Slotify Server itself can't connect to Mosquitto.",
                "Ayusin muna ang Mosquitto sa taas, tapos pindutin ang Retry sa main window."));
    }

    // ---- 2. Windows Firewall ----------------------------------------------

    private static void CheckFirewall(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "Windows Firewall";

        if (s.Profile == NetworkProfile.Public)
        {
            r.Add(new(area, "Network profile", CheckStatus.Warn,
                "This network is set to PUBLIC. Windows is strictest on Public networks.",
                "Gawing Private: Settings → Network & Internet → (ang network mo) → Network profile type → Private."));
        }
        else if (s.Profile != NetworkProfile.Unknown)
        {
            r.Add(new(area, "Network profile", CheckStatus.Pass, $"{s.Profile}."));
        }

        var fw = s.Firewall;
        if (!fw.Readable)
        {
            r.Add(new(area, "Firewall rules", CheckStatus.Warn,
                "Couldn't read the Windows Firewall settings.",
                "Siguraduhing allowed ang inbound TCP 1883 at UDP 5353 sa server PC. Kung may antivirus (hal. Kaspersky, ESET), i-allow din doon."));
            return;
        }

        if (fw.FirewallOn == false)
        {
            r.Add(new(area, "Firewall rules", CheckStatus.Pass,
                "Windows Firewall is off for this network, so it isn't blocking Slotify."));
            return;
        }

        r.Add(fw.MqttRuleOk == true
            ? new(area, "Inbound TCP 1883 (MQTT)", CheckStatus.Pass, "Allowed by the Slotify firewall rule.")
            : new(area, "Inbound TCP 1883 (MQTT)", CheckStatus.Fail,
                "Slotify's firewall rule for TCP 1883 is missing, disabled, or doesn't cover this network.",
                "I-reinstall ang Slotify Server, o i-run as admin: netsh advfirewall firewall add rule name=\"RentalSystem Broker (MQTT 1883)\" dir=in action=allow protocol=TCP localport=1883 profile=any"));

        r.Add(fw.MdnsRuleOk == true
            ? new(area, "Inbound UDP 5353 (auto-detect)", CheckStatus.Pass, "Allowed by the Slotify firewall rule.")
            : new(area, "Inbound UDP 5353 (auto-detect)", CheckStatus.Warn,
                "Slotify's mDNS rule is missing or disabled. Tablets and the coin box may not find the server automatically.",
                "I-reinstall ang Slotify Server, o i-run as admin: netsh advfirewall firewall add rule name=\"RentalSystem mDNS (UDP 5353)\" dir=in action=allow protocol=UDP localport=5353 profile=any"));

        r.Add(new(area, "Antivirus", CheckStatus.Info,
            "A third-party antivirus or firewall can still block TCP 1883 even when Windows allows it.",
            "Kung may antivirus sa server PC at hindi pa rin maka-connect ang PCs, i-allow ang Mosquitto / port 1883 doon."));
    }

    // ---- 3. Network --------------------------------------------------------

    private static void CheckNetwork(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "Network";

        var real = s.Adapters.Where(a => !a.LooksVirtual && !IsApipa(a.Ipv4)).ToList();
        var withGateway = real.Where(a => a.HasGateway).ToList();
        var virtualOnes = s.Adapters.Where(a => a.LooksVirtual).ToList();

        if (real.Count == 0)
        {
            r.Add(new(area, "LAN connection", CheckStatus.Fail,
                "No working network adapter with a LAN IP was found.",
                "I-check ang LAN cable / Wi-Fi ng server PC. Dapat may IP ito galing sa router (hal. 192.168.x.x)."));
        }
        else
        {
            r.Add(new(area, "Server IP address(es)", CheckStatus.Info,
                string.Join("; ", real.Select(a => $"{a.Name}: {a.Ipv4}{(a.HasGateway ? "" : " (no gateway)")}"))));
        }

        if (string.IsNullOrWhiteSpace(s.AdvertisedIp))
        {
            r.Add(new(area, "Address clients should use", CheckStatus.Fail,
                "The server has no LAN address to give the PCs, tablets and coin box.",
                "Ikonekta ang server PC sa router, tapos i-restart ang Slotify Server."));
        }
        else if (IsApipa(s.AdvertisedIp) || s.AdvertisedIp is "127.0.0.1" or "localhost")
        {
            r.Add(new(area, "Address clients should use", CheckStatus.Fail,
                $"The server is advertising {s.AdvertisedIp}, which other devices can't reach.",
                "Ayusin ang LAN ng server PC (dapat 192.168.x.x o 10.x.x.x), tapos i-restart ang Slotify Server."));
        }
        else
        {
            r.Add(new(area, "Address clients should use", CheckStatus.Info,
                $"{s.AdvertisedIp}:{s.MqttPort}" + (s.AdvertiseHostOverridden ? " (set by advertiseHost in appsettings.json)" : ""),
                "Kung ayaw gumana ng \"auto\" sa isang PC, i-type ito sa client installer bilang server IP."));
        }

        if (!string.IsNullOrWhiteSpace(s.AdvertisedIp) && !s.AdvertiseHostOverridden && s.Adapters.Count > 0 &&
            s.Adapters.All(a => a.Ipv4 != s.AdvertisedIp))
        {
            r.Add(new(area, "Server IP changed", CheckStatus.Fail,
                $"Slotify is still advertising {s.AdvertisedIp}, but this PC no longer has that IP. Devices on \"auto\" are told the wrong address.",
                "I-restart ang Slotify Server. Para hindi na maulit, mag-set ng DHCP reservation (fixed IP) sa router para sa server PC (Owner Guide §4)."));
        }

        var multiple = withGateway.Count > 1 || (real.Count > 1 && withGateway.Count == 0);
        if ((multiple || virtualOnes.Count > 0) && !s.AdvertiseHostOverridden)
        {
            var others = s.Adapters.Where(a => a.Ipv4 != s.AdvertisedIp).Select(a => $"{a.Name} ({a.Ipv4})");
            r.Add(new(area, "More than one network", CheckStatus.Warn,
                $"Besides {s.AdvertisedIp ?? "the LAN"}, this PC also has: {string.Join(", ", others)}. Auto-detect could pick the wrong one.",
                "Kung hindi makita ng PCs ang server, i-disable ang hindi ginagamit na adapter (VPN, VirtualBox, Hyper-V), o i-set ang \"advertiseHost\" sa appsettings.json ng server."));
        }
    }

    // ---- 4. Discovery ------------------------------------------------------

    private static void CheckDiscovery(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "Auto-detect (discovery)";

        r.Add(s.UdpDiscoveryRunning
            ? new(area, "LAN broadcast (PCs)", CheckStatus.Pass, "Broadcasting the server address every 10 seconds on UDP 1884.")
            : new(area, "LAN broadcast (PCs)", CheckStatus.Fail,
                "Not running. PCs set to \"auto\" can't find the server.",
                "I-restart ang Slotify Server. Habang hindi pa ayos, i-type ang server IP sa client installer imbes na \"auto\"."));

        r.Add(s.MdnsRunning
            ? new(area, "mDNS (tablets, coin box)", CheckStatus.Pass, "Advertising _slotify-mqtt._tcp.")
            : new(area, "mDNS (tablets, coin box)", CheckStatus.Warn,
                "Not running. Tablets and the coin box find the server through mDNS.",
                "I-restart ang Slotify Server. Kung ganoon pa rin, baka may ibang app na gumagamit ng UDP 5353 (hal. iTunes/Bonjour)."));
    }

    // ---- 5. Units ----------------------------------------------------------

    private static void CheckUnits(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "PCs and tablets";

        AddUnitGroup(s, r, area, "PCs", s.Units.Where(u => u.DeviceType == "pc").ToList(),
            "Wala pang PC. I-install ang Slotify Client sa isang PC (server: auto). Owner Guide §6.");
        AddUnitGroup(s, r, area, "Tablets / phones", s.Units.Where(u => u.DeviceType != "pc").ToList(),
            "");
    }

    private static void AddUnitGroup(
        SetupSnapshot s, List<SetupCheckResult> r, string area, string label,
        List<UnitInfo> units, string fixWhenNone)
    {
        if (units.Count == 0)
        {
            r.Add(new(area, label, string.IsNullOrEmpty(fixWhenNone) ? CheckStatus.Info : CheckStatus.Warn,
                "None registered yet.", fixWhenNone));
            return;
        }

        var online = units.Count(u => u.Online);
        var offline = units.Where(u => !u.Online).ToList();
        var counts = $"{online} of {units.Count} online";

        if (offline.Count == 0)
        {
            r.Add(new(area, label, CheckStatus.Pass, counts + "."));
            return;
        }

        var offlineText = string.Join(", ", offline
            .OrderBy(u => u.StationId, StringComparer.OrdinalIgnoreCase)
            .Select(u => $"{u.StationId} (last seen {Ago(u.LastSeen, s.Now)})"));
        r.Add(new(area, label, online == 0 ? CheckStatus.Fail : CheckStatus.Warn,
            $"{counts}. Offline: {offlineText}.",
            "Normal lang kung naka-off ang unit. Kung naka-on pero offline: i-check ang LAN cable / Wi-Fi nito, at tingnan ang hint sa lock screen nito.",
            ReportDetail: $"{counts}."));
    }

    // ---- 6. Coin box -------------------------------------------------------

    private static void CheckCoinBox(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "Coin box";

        if (s.CoinBoxes.Count == 0)
        {
            r.Add(new(area, "Coin box", CheckStatus.Warn,
                "No coin box has connected yet.",
                "I-flash ang coin box at ikonekta sa shop Wi-Fi gamit ang hotspot nito (Slotify-CoinBox-XXXX). Owner Guide §3."));
            return;
        }

        foreach (var box in s.CoinBoxes.OrderBy(b => b.AcceptorId, StringComparer.OrdinalIgnoreCase))
        {
            var fw = string.IsNullOrWhiteSpace(box.FirmwareVersion) ? "firmware ?" : $"firmware {box.FirmwareVersion}";
            r.Add(box.Online
                ? new(area, box.AcceptorId, CheckStatus.Pass, $"Online, {fw}.")
                : new(area, box.AcceptorId, CheckStatus.Fail,
                    $"OFFLINE (last seen {Ago(box.LastSeen, s.Now)}), {fw}. Customers can't insert coins.",
                    "I-check kung may power ang coin box at abot ng Wi-Fi. Kung nag-hotspot ito (Slotify-CoinBox-XXXX), i-setup ulit ang Wi-Fi gamit ang phone."));
        }
    }

    // ---- 7. Security -------------------------------------------------------

    private static void CheckSecurity(SetupSnapshot s, List<SetupCheckResult> r)
    {
        const string area = "Security";

        r.Add(s.DefaultAdminPassword switch
        {
            true => new(area, "Admin password", CheckStatus.Warn,
                "Still the public default. Anyone who knows it can open maintenance mode on your PCs and tablets.",
                "Palitan: Setup → Tablet Admin. Isang password ito para sa lahat ng PC at tablet."),
            false => new(area, "Admin password", CheckStatus.Pass, "Changed from the default."),
            _ => new(area, "Admin password", CheckStatus.Info, "Couldn't check the admin password.")
        });
    }

    // ---- Report -------------------------------------------------------------

    /// <summary>
    /// Short plain-text summary for pasting into the Slotify Facebook group.
    /// Contains no passwords and no station names (counts only).
    /// </summary>
    public static string BuildReport(SetupSnapshot s, IReadOnlyList<SetupCheckResult> results)
    {
        var sb = new StringBuilder();
        var fails = results.Count(x => x.Status == CheckStatus.Fail);
        var warns = results.Count(x => x.Status == CheckStatus.Warn);

        sb.AppendLine($"Slotify Check setup - server v{s.ServerVersion} - {s.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Result: {fails} problem(s), {warns} warning(s)");

        foreach (var x in results)
        {
            if (x.Area == "Windows Firewall" && x.Title == "Antivirus")
            {
                continue; // generic reminder, not a finding
            }

            var tag = x.Status switch
            {
                CheckStatus.Pass => "OK  ",
                CheckStatus.Info => "INFO",
                CheckStatus.Warn => "WARN",
                _ => "FAIL"
            };
            sb.AppendLine($"[{tag}] {x.Area} / {x.Title}: {x.ReportDetail ?? x.Detail}");
        }

        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    private static bool IsLoopback(string address) =>
        address is "127.0.0.1" or "::1" || address.StartsWith("127.", StringComparison.Ordinal);

    private static bool IsApipa(string? address) =>
        address is not null && address.StartsWith("169.254.", StringComparison.Ordinal);

    internal static string Ago(DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (lastSeen is null)
        {
            return "never";
        }

        var span = now - lastSeen.Value;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}
