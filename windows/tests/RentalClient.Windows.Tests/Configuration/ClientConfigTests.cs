using RentalClient.Windows.Configuration;

namespace RentalClient.Windows.Tests.Configuration;

public sealed class ClientConfigTests
{
    [Fact]
    public void ResolveStationIdFromHostname_returns_self_when_explicit()
    {
        var config = NewConfig(stationId: "pc-07");

        var resolved = config.ResolveStationIdFromHostname();

        Assert.Same(config, resolved);
        Assert.Equal("pc-07", resolved.StationId);
    }

    [Fact]
    public void ResolveStationIdFromHostname_uses_hostname_when_auto()
    {
        var config = NewConfig(stationId: "auto");

        var resolved = config.ResolveStationIdFromHostname();

        Assert.Equal(
            Environment.MachineName.Trim().ToLowerInvariant(),
            resolved.StationId);
    }

    [Fact]
    public void ResolveStationIdFromHostname_uses_hostname_when_AUTO_case_insensitive()
    {
        var config = NewConfig(stationId: "AUTO");

        var resolved = config.ResolveStationIdFromHostname();

        Assert.Equal(
            Environment.MachineName.Trim().ToLowerInvariant(),
            resolved.StationId);
    }

    [Fact]
    public void ResolveStationIdFromHostname_uses_hostname_when_empty()
    {
        var config = NewConfig(stationId: "");

        var resolved = config.ResolveStationIdFromHostname();

        Assert.Equal(
            Environment.MachineName.Trim().ToLowerInvariant(),
            resolved.StationId);
    }

    [Fact]
    public void ResolveStationIdFromHostname_uses_hostname_when_whitespace()
    {
        var config = NewConfig(stationId: "   ");

        var resolved = config.ResolveStationIdFromHostname();

        Assert.Equal(
            Environment.MachineName.Trim().ToLowerInvariant(),
            resolved.StationId);
    }

    [Fact]
    public void ResolveStationIdFromHostname_is_idempotent()
    {
        var config = NewConfig(stationId: "auto");

        var firstPass = config.ResolveStationIdFromHostname();
        var secondPass = firstPass.ResolveStationIdFromHostname();

        Assert.Same(firstPass, secondPass);
        Assert.Equal(firstPass.StationId, secondPass.StationId);
    }

    private static ClientConfig NewConfig(string stationId) =>
        new ClientConfig
        {
            StationId = stationId,
            DeviceType = "pc",
            AppVersion = "1.0.0",
            Broker = new BrokerConfig { Host = "127.0.0.1", Port = 1883 },
            Debug = new DebugConfig(),
            Connection = new ConnectionConfig
            {
                ReconnectIntervalSec = 5,
                OfflineUntrustedAfterSec = 120
            },
            CountdownWarning = new CountdownWarningConfig
            {
                Enabled = true,
                FirstWarningSec = 60,
                FinalCountdownStartSec = 10
            }
        };
}
