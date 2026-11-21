using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RentalShared.Discovery;

namespace RentalServer;

/// <summary>
/// Broadcasts the broker host/port to the LAN every ~10 s so clients configured
/// with broker.host="auto" can find the server without a hardcoded IP.
/// </summary>
public sealed class UdpDiscoveryBroadcaster : IDisposable
{
    private const int Port = 1884;
    private const int IntervalMs = 10_000;

    private readonly string _brokerHost;
    private readonly int _brokerPort;
    private readonly string _appVersion;
    private readonly byte[] _payload;
    private readonly IPAddress? _bindAddress;
    private readonly IPEndPoint[] _endpoints;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UdpDiscoveryBroadcaster(string brokerHost, int brokerPort, string appVersion)
    {
        _brokerHost = brokerHost;
        _brokerPort = brokerPort;
        _appVersion = appVersion;

        var message = new RentalDiscoveryMessage
        {
            BrokerHost = brokerHost,
            BrokerPort = brokerPort,
            AppVersion = appVersion
        };

        var json = JsonSerializer.Serialize(message);
        _payload = Encoding.UTF8.GetBytes(json);

        _bindAddress = TryGetLocalAddress(brokerHost);
        _endpoints = BuildBroadcastEndpoints(_bindAddress);
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(RunBroadcastLoopAsync);
    }

    private async Task RunBroadcastLoopAsync()
    {
        using var udp = CreateUdpClient();
        udp.EnableBroadcast = true;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                foreach (var endpoint in _endpoints)
                {
                    try
                    {
                        await udp.SendAsync(_payload, _payload.Length, endpoint);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[discovery] Broadcast to {endpoint} failed: {ex.Message}");
                    }
                }

                try
                {
                    await Task.Delay(IntervalMs, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            udp.Close();
        }
    }

    private UdpClient CreateUdpClient()
    {
        if (_bindAddress is null)
        {
            return new UdpClient();
        }

        try
        {
            return new UdpClient(new IPEndPoint(_bindAddress, 0));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[discovery] Could not bind UDP broadcaster to {_bindAddress}: {ex.Message}");
            return new UdpClient();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private static IPAddress? TryGetLocalAddress(string brokerHost)
    {
        if (!IPAddress.TryParse(brokerHost, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in ni.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    address.Address.Equals(parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static IPEndPoint[] BuildBroadcastEndpoints(IPAddress? bindAddress)
    {
        var endpoints = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, Port)
        };

        if (bindAddress is not null)
        {
            var directed = TryGetDirectedBroadcast(bindAddress);
            if (directed is not null && !directed.Equals(IPAddress.Broadcast))
            {
                endpoints.Add(new IPEndPoint(directed, Port));
            }
        }

        return endpoints
            .DistinctBy(endpoint => endpoint.Address)
            .ToArray();
    }

    private static IPAddress? TryGetDirectedBroadcast(IPAddress localAddress)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in ni.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    !address.Address.Equals(localAddress) ||
                    address.IPv4Mask is null)
                {
                    continue;
                }

                var ipBytes = address.Address.GetAddressBytes();
                var maskBytes = address.IPv4Mask.GetAddressBytes();
                var broadcastBytes = new byte[4];

                for (var i = 0; i < broadcastBytes.Length; i++)
                {
                    broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                }

                return new IPAddress(broadcastBytes);
            }
        }

        return null;
    }
}
