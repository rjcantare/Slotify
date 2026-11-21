using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

public sealed record PcPolicyConfigMessage
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;

    [JsonPropertyName("autoCloseOnLock")]
    public PcAutoCloseOnLockPolicyMessage AutoCloseOnLock { get; init; } = new();

    [JsonPropertyName("idleShutdown")]
    public PcIdleShutdownPolicyMessage IdleShutdown { get; init; } = new();
}

public sealed record PcAutoCloseOnLockPolicyMessage
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

public sealed record PcIdleShutdownPolicyMessage
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("idleMinutes")]
    public int IdleMinutes { get; init; } = 5;
}
