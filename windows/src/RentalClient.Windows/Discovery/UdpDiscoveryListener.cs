using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RentalShared.Discovery;

namespace RentalClient.Windows.Discovery;

/// <summary>
/// Listens for the server's UDP discovery broadcast on port 1884.
/// Called once at startup to resolve the broker host when the config
/// has broker.host="auto". Returns the discovered endpoint or null
/// if no broadcast is received within the timeout window.
/// </summary>
public static class UdpDiscoveryListener
{
    private const int Port = 1884;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Waits for a <see cref="RentalDiscoveryMessage"/> on UDP port 1884.
    /// Returns the broker host and port, or null if nothing is received
    /// within 5 seconds.
    /// </summary>
    public static async Task<DiscoveryResult?> DiscoverAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        using var udp = new UdpClient(Port);
        udp.EnableBroadcast = true;

        try
        {
            using var cts = new CancellationTokenSource(effectiveTimeout);
            var receive = udp.ReceiveAsync().WaitAsync(cts.Token);

            var result = await receive;
            var json = Encoding.UTF8.GetString(result.Buffer);

            var message = JsonSerializer.Deserialize<RentalDiscoveryMessage>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (message is null || message.Type != RentalDiscoveryMessage.MessageType)
            {
                return null;
            }

            return new DiscoveryResult(message.BrokerHost, message.BrokerPort);
        }
        catch (OperationCanceledException)
        {
            // Timeout — no server broadcasting on the LAN.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            udp.Close();
        }
    }
}

public sealed record DiscoveryResult(string BrokerHost, int BrokerPort);
