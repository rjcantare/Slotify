namespace RentalShared.Discovery;

/// <summary>
/// UDP broadcast payload sent by the server every ~10 s so LAN clients
/// can auto-discover the MQTT broker address without hardcoding an IP.
/// Guardrail: this carries only the broker endpoint. The MQTT protocol,
/// schemas, QoS, retain, ACK, and dedup contract are unchanged.
/// </summary>
public sealed record RentalDiscoveryMessage
{
    public const string MessageType = "rental-discovery";

    public string Type { get; init; } = MessageType;
    public string BrokerHost { get; init; } = string.Empty;
    public int BrokerPort { get; init; } = 1883;
    public string AppVersion { get; init; } = string.Empty;
}
