namespace RentalServer.Core.Rates;

public sealed record RateSheet(
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> AddTimeRates,
    IReadOnlyDictionary<string, InsertCoinRateRule> InsertCoinRules);
