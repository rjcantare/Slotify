using RentalServer.Core.SetupCheck;

namespace RentalServer.Core.Tests.SetupCheck;

public sealed class SetupCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(8));

    // A healthy single-NIC shop server.
    private static SetupSnapshot Healthy() => new()
    {
        ServerVersion = "1.3.3",
        Now = Now,
        Mosquitto = ServiceState.Running,
        MqttListenAddresses = new[] { "0.0.0.0", "127.0.0.1" },
        ServerConnectedToBroker = true,
        Profile = NetworkProfile.Private,
        Firewall = new FirewallInfo(Readable: true, FirewallOn: true, MqttRuleOk: true, MdnsRuleOk: true),
        Adapters = new[] { new AdapterInfo("Ethernet", "192.168.1.10", HasGateway: true, LooksVirtual: false) },
        AdvertisedIp = "192.168.1.10",
        UdpDiscoveryRunning = true,
        MdnsRunning = true,
        Units = new[]
        {
            new UnitInfo("pc-01", "pc", true, Now, "1.3.3"),
            new UnitInfo("tab-01", "tab", true, Now, "1.3.3")
        },
        CoinBoxes = new[] { new CoinBoxInfo("coin-01", true, Now, "1.3.3") },
        DefaultAdminPassword = false
    };

    private static SetupCheckResult Find(IReadOnlyList<SetupCheckResult> results, string title) =>
        Assert.Single(results, r => r.Title == title);

    [Fact]
    public void Healthy_setup_has_no_failures_or_warnings()
    {
        var results = SetupChecker.Evaluate(Healthy());

        Assert.DoesNotContain(results, r => r.Status is CheckStatus.Fail or CheckStatus.Warn);
    }

    [Theory]
    [InlineData(ServiceState.Stopped, CheckStatus.Fail)]
    [InlineData(ServiceState.NotInstalled, CheckStatus.Fail)]
    [InlineData(ServiceState.Unknown, CheckStatus.Warn)]
    [InlineData(ServiceState.Running, CheckStatus.Pass)]
    public void Mosquitto_service_state_maps_to_status(ServiceState state, CheckStatus expected)
    {
        var results = SetupChecker.Evaluate(Healthy() with { Mosquitto = state });

        Assert.Equal(expected, Find(results, "Mosquitto service").Status);
    }

    [Fact]
    public void Broker_listening_only_on_localhost_fails_with_fix()
    {
        var results = SetupChecker.Evaluate(Healthy() with { MqttListenAddresses = new[] { "127.0.0.1", "::1" } });

        var check = Find(results, "Port 1883 listening");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("listener 1883 0.0.0.0", check.Fix);
    }

    [Fact]
    public void Nothing_listening_fails()
    {
        var results = SetupChecker.Evaluate(Healthy() with { MqttListenAddresses = Array.Empty<string>() });

        Assert.Equal(CheckStatus.Fail, Find(results, "Port 1883 listening").Status);
    }

    [Fact]
    public void Public_network_profile_warns()
    {
        var results = SetupChecker.Evaluate(Healthy() with { Profile = NetworkProfile.Public });

        var check = Find(results, "Network profile");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("Private", check.Fix);
    }

    [Fact]
    public void Missing_mqtt_firewall_rule_fails_with_netsh_fix()
    {
        var results = SetupChecker.Evaluate(Healthy() with
        {
            Firewall = new FirewallInfo(true, true, MqttRuleOk: false, MdnsRuleOk: true)
        });

        var check = Find(results, "Inbound TCP 1883 (MQTT)");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("localport=1883", check.Fix);
    }

    [Fact]
    public void Firewall_off_skips_rule_checks()
    {
        var results = SetupChecker.Evaluate(Healthy() with
        {
            Firewall = new FirewallInfo(true, FirewallOn: false, MqttRuleOk: false, MdnsRuleOk: false)
        });

        Assert.DoesNotContain(results, r => r.Title == "Inbound TCP 1883 (MQTT)");
        Assert.Equal(CheckStatus.Pass, Find(results, "Firewall rules").Status);
    }

    [Fact]
    public void Virtual_adapter_warns_about_wrong_pick()
    {
        var results = SetupChecker.Evaluate(Healthy() with
        {
            Adapters = new[]
            {
                new AdapterInfo("Ethernet", "192.168.1.10", true, false),
                new AdapterInfo("VirtualBox Host-Only", "192.168.56.1", false, true)
            }
        });

        var check = Find(results, "More than one network");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("192.168.56.1", check.Detail);
    }

    [Fact]
    public void Advertise_host_override_suppresses_multi_adapter_warning()
    {
        var results = SetupChecker.Evaluate(Healthy() with
        {
            AdvertiseHostOverridden = true,
            Adapters = new[]
            {
                new AdapterInfo("Ethernet", "192.168.1.10", true, false),
                new AdapterInfo("Wi-Fi", "192.168.0.5", true, false)
            }
        });

        Assert.DoesNotContain(results, r => r.Title == "More than one network");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("169.254.10.20")]
    [InlineData("127.0.0.1")]
    public void Unreachable_advertised_address_fails(string? advertised)
    {
        var results = SetupChecker.Evaluate(Healthy() with { AdvertisedIp = advertised });

        Assert.Equal(CheckStatus.Fail, Find(results, "Address clients should use").Status);
    }

    [Fact]
    public void Advertised_ip_no_longer_on_this_pc_fails()
    {
        var results = SetupChecker.Evaluate(Healthy() with
        {
            Adapters = new[] { new AdapterInfo("Ethernet", "192.168.1.25", true, false) }
        });

        var check = Find(results, "Server IP changed");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("192.168.1.10", check.Detail);
    }

    [Fact]
    public void Discovery_not_running_fails_for_pcs_and_warns_for_mdns()
    {
        var results = SetupChecker.Evaluate(Healthy() with { UdpDiscoveryRunning = false, MdnsRunning = false });

        Assert.Equal(CheckStatus.Fail, Find(results, "LAN broadcast (PCs)").Status);
        Assert.Equal(CheckStatus.Warn, Find(results, "mDNS (tablets, coin box)").Status);
    }

    [Fact]
    public void Some_pcs_offline_warns_and_names_them_with_last_seen()
    {
        var results = SetupChecker.Evaluate(Healthy() with
        {
            Units = new[]
            {
                new UnitInfo("pc-01", "pc", true, Now, "1.3.3"),
                new UnitInfo("pc-02", "pc", false, Now.AddHours(-3), "1.3.3")
            }
        });

        var check = Find(results, "PCs");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("pc-02 (last seen 3 h ago)", check.Detail);
        Assert.DoesNotContain("pc-02", check.ReportDetail);
    }

    [Fact]
    public void All_pcs_offline_fails_and_no_pcs_warns()
    {
        var allOff = SetupChecker.Evaluate(Healthy() with
        {
            Units = new[] { new UnitInfo("pc-01", "pc", false, null, null) }
        });
        Assert.Equal(CheckStatus.Fail, Find(allOff, "PCs").Status);

        var none = SetupChecker.Evaluate(Healthy() with { Units = Array.Empty<UnitInfo>() });
        Assert.Equal(CheckStatus.Warn, Find(none, "PCs").Status);
        Assert.Equal(CheckStatus.Info, Find(none, "Tablets / phones").Status);
    }

    [Fact]
    public void Coin_box_offline_fails_and_missing_warns()
    {
        var offline = SetupChecker.Evaluate(Healthy() with
        {
            CoinBoxes = new[] { new CoinBoxInfo("coin-01", false, Now.AddMinutes(-5), "1.3.2") }
        });
        var check = Find(offline, "coin-01");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("5 min ago", check.Detail);
        Assert.Contains("1.3.2", check.Detail);

        var none = SetupChecker.Evaluate(Healthy() with { CoinBoxes = Array.Empty<CoinBoxInfo>() });
        Assert.Equal(CheckStatus.Warn, Find(none, "Coin box").Status);
    }

    [Fact]
    public void Default_admin_password_warns()
    {
        var results = SetupChecker.Evaluate(Healthy() with { DefaultAdminPassword = true });

        var check = Find(results, "Admin password");
        Assert.Equal(CheckStatus.Warn, check.Status);
        Assert.Contains("Tablet Admin", check.Fix);
    }

    [Fact]
    public void Report_has_counts_but_no_station_names_or_passwords()
    {
        var snapshot = Healthy() with
        {
            DefaultAdminPassword = true,
            Units = new[]
            {
                new UnitInfo("JUAN-PC", "pc", false, Now.AddDays(-2), "1.3.3"),
                new UnitInfo("pc-02", "pc", true, Now, "1.3.3")
            }
        };
        var results = SetupChecker.Evaluate(snapshot);

        var report = SetupChecker.BuildReport(snapshot, results);

        Assert.Contains("server v1.3.3", report);
        Assert.Contains("1 of 2 online", report);
        Assert.Contains("[WARN] Security / Admin password", report);
        Assert.DoesNotContain("JUAN-PC", report);
        Assert.DoesNotContain("pc-02", report);
        Assert.DoesNotContain("admin@123", report);
        Assert.DoesNotContain("Antivirus", report);
    }
}
