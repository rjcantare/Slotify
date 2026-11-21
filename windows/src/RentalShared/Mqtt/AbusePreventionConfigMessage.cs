using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Retained payload on `rental/config/abuse-prevention`. Server publishes on startup
// and after every admin edit. Clients subscribe to know the current abuse rules.
//
// JSON shape:
//   { "enabled": true, "maxEmptyClaims": 3, "banMinutes": 2, "updatedAt": "..." }
//
// When `enabled` is false, no abuse tracking or banning occurs server-side
// and the clients hide any ban-related UI.
public sealed record AbusePreventionConfigMessage
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("maxEmptyClaims")]
    public int MaxEmptyClaims { get; init; } = 3;

    [JsonPropertyName("banMinutes")]
    public int BanMinutes { get; init; } = 2;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;
}
