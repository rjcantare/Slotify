using System.Collections.Concurrent;
using System.Globalization;

namespace RentalServer.Core.Abuse;

public sealed class AbusePreventionService
{
    public sealed record Config(bool Enabled, int MaxEmptyClaims, int BanMinutes);

    public sealed record BanInfo(bool IsBanned, string? BanUntilUtc);

    public static Config DefaultConfig { get; } = new(Enabled: false, MaxEmptyClaims: 3, BanMinutes: 2);

    private Config _config = DefaultConfig;
    private readonly ConcurrentDictionary<string, int> _emptyClaimCounts = new();
    private readonly ConcurrentDictionary<string, string> _banExpirations = new(); // stationId -> banUntilUtc

    public Config CurrentConfig => _config;

    public void UpdateConfig(Config config)
    {
        ValidateConfig(config);
        _config = config;

        if (!config.Enabled)
        {
            _emptyClaimCounts.Clear();
            _banExpirations.Clear();
        }
    }

    public BanInfo CheckBan(string stationId)
    {
        if (!_config.Enabled)
        {
            return new BanInfo(false, null);
        }

        if (_banExpirations.TryGetValue(stationId, out var banUntil))
        {
            var now = DateTimeOffset.UtcNow;
            if (DateTimeOffset.TryParse(banUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var banUntilDto)
                && banUntilDto > now)
            {
                return new BanInfo(true, banUntil);
            }

            _banExpirations.TryRemove(stationId, out _);
            _emptyClaimCounts.TryRemove(stationId, out _);
        }

        return new BanInfo(false, null);
    }

    public void RecordEmptyClaim(string stationId)
    {
        if (!_config.Enabled)
        {
            return;
        }

        var count = _emptyClaimCounts.AddOrUpdate(stationId, 1, (_, current) => current + 1);

        if (count >= _config.MaxEmptyClaims)
        {
            var banUntil = DateTimeOffset.UtcNow.AddMinutes(_config.BanMinutes);
            _banExpirations[stationId] = banUntil.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture);
            _emptyClaimCounts[stationId] = 0;
        }
    }

    public void ResetOnCoin(string stationId)
    {
        _emptyClaimCounts.TryRemove(stationId, out _);
        _banExpirations.TryRemove(stationId, out _);
    }

    public static void ValidateConfig(Config config)
    {
        if (config.MaxEmptyClaims < 1)
        {
            throw new ArgumentException("Max empty claims must be at least 1.", nameof(config));
        }

        if (config.BanMinutes < 1)
        {
            throw new ArgumentException("Ban minutes must be at least 1.", nameof(config));
        }
    }
}
