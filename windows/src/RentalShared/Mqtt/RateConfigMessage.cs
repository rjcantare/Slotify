using System.Text.Json.Serialization;

namespace RentalShared.Mqtt;

// Retained payload on `rental/config/rates`. Server publishes on every rate
// change AND on startup so freshly-connected clients always see the latest
// version.
//
// JSON shape per protocol §11:
//   { "version": 3,
//     "rates": { "pc": {"1":360,"5":1800,...}, "phn": {...}, "tab": {...} },
//     "addTimeRates": { ... },
//     "insertCoinRules": { "pc": {"starterMaxPesos":7,"starterSeconds":1800,
//                                  "normalizedFromPesos":10,"perPesoSeconds":360}, ... },
//     "updatedAt": "..." }
//
// Denomination keys are strings in the wire format because JSON object keys are
// always strings even when they look numeric. The server's internal Dictionary
// uses int denomination keys; conversion happens in RateService.
public sealed record RateConfigMessage
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("rates")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Rates { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();

    [JsonPropertyName("addTimeRates")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> AddTimeRates { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();

    [JsonPropertyName("insertCoinRules")]
    public IReadOnlyDictionary<string, InsertCoinRateRuleMessage> InsertCoinRules { get; init; } =
        new Dictionary<string, InsertCoinRateRuleMessage>();

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;
}

public sealed record InsertCoinRateRuleMessage
{
    // Minimum total pesos needed for the starter unlock. Totals below this do not
    // add time yet; they only remain part of the current coin-window total.
    [JsonPropertyName("starterMaxPesos")]
    public int StarterMaxPesos { get; init; }

    [JsonPropertyName("starterSeconds")]
    public int StarterSeconds { get; init; }

    [JsonPropertyName("normalizedFromPesos")]
    public int NormalizedFromPesos { get; init; }

    [JsonPropertyName("perPesoSeconds")]
    public int PerPesoSeconds { get; init; }
}
