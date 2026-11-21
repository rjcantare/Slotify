using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

public sealed record TabletAdminConfigMessage
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = string.Empty;

    [JsonPropertyName("passwordSalt")]
    public string PasswordSalt { get; init; } = string.Empty;

    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; init; } = string.Empty;
}
