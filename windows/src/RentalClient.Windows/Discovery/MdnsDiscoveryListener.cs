using System.Net;
using Makaretu.Dns;
using RentalShared.Discovery;

namespace RentalClient.Windows.Discovery;

/// <summary>
/// Resolves the MQTT broker via mDNS / DNS-SD (<c>_slotify-mqtt._tcp</c>) — the
/// same path the Wi-Fi tablets and ESP32 use. On the wired diskless PCs the
/// server's UDP broadcast also works, so <see cref="StationMqttClient"/> tries
/// mDNS first and falls back to <see cref="UdpDiscoveryListener"/>. Reuses
/// <see cref="DiscoveryResult"/> so both paths are interchangeable.
/// Guardrail: resolves only the broker host/port — the MQTT contract is unchanged.
/// </summary>
public static class MdnsDiscoveryListener
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Browses for the Slotify broker service and resolves the first match to a
    /// host/port. Returns null on timeout or error.
    /// </summary>
    public static async Task<DiscoveryResult?> DiscoverAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var tcs = new TaskCompletionSource<DiscoveryResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ServiceDiscovery? sd = null;
        try
        {
            sd = new ServiceDiscovery();
            sd.ServiceInstanceDiscovered += (_, e) =>
            {
                try
                {
                    if (!e.ServiceInstanceName.ToString()
                            .Contains("slotify-mqtt", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var result = ExtractEndpoint(e.Message);
                    if (result is not null)
                    {
                        tcs.TrySetResult(result);
                    }
                }
                catch
                {
                    // Ignore a malformed announcement; wait for the next or time out.
                }
            };

            sd.QueryServiceInstances(MdnsDiscovery.ServiceType);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(effectiveTimeout));
            return completed == tcs.Task ? await tcs.Task : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            sd?.Dispose();
        }
    }

    /// <summary>
    /// Pulls the broker endpoint out of a discovered announcement. Prefers the
    /// explicit TXT brokerHost/brokerPort the server sets; falls back to the
    /// SRV port + A-record address the SRV/A chain resolves to.
    /// </summary>
    private static DiscoveryResult? ExtractEndpoint(Makaretu.Dns.Message msg)
    {
        var records = msg.AdditionalRecords;

        string? host = null;
        var port = 0;

        var txt = records.OfType<TXTRecord>().FirstOrDefault();
        if (txt is not null)
        {
            foreach (var s in txt.Strings)
            {
                var eq = s.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = s[..eq];
                var val = s[(eq + 1)..];
                if (key.Equals(MdnsDiscovery.TxtBrokerHost, StringComparison.OrdinalIgnoreCase))
                {
                    host = val;
                }
                else if (key.Equals(MdnsDiscovery.TxtBrokerPort, StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(val, out var p))
                {
                    port = p;
                }
            }
        }

        if (port == 0)
        {
            port = records.OfType<SRVRecord>().FirstOrDefault()?.Port ?? 0;
        }

        host ??= records.OfType<ARecord>().FirstOrDefault()?.Address?.ToString();

        return host is not null && port > 0 ? new DiscoveryResult(host, port) : null;
    }
}
