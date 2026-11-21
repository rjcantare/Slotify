using System.Globalization;
using System.Text.Json;
using RentalServer.Core.EventLog;
using RentalShared.Mqtt;

namespace RentalServer.Core.Rates;

public sealed class RateService
{
    public static readonly IReadOnlyList<string> SupportedDeviceTypes =
        new[] { "pc", "phn", "tab" };

    public static readonly IReadOnlyList<int> SupportedAddTimeDenominations =
        new[] { 1, 5, 10, 20 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly RateRepository _repository;
    private readonly IRatesConfigPublisher _publisher;
    private readonly IEventLogger? _eventLogger;

    public RateService(
        RateRepository repository,
        IRatesConfigPublisher publisher,
        IEventLogger? eventLogger = null)
    {
        _repository = repository;
        _publisher = publisher;
        _eventLogger = eventLogger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _repository.InitializeAsync(cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>?> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.GetCurrentRatesAsync(cancellationToken);
    }

    public Task<RateSheet?> GetCurrentRateSheetAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetCurrentRateSheetAsync(cancellationToken);
    }

    public async Task PublishCurrentAsync(CancellationToken cancellationToken = default)
    {
        var version = await _repository.GetCurrentVersionAsync(cancellationToken);
        if (version is null)
        {
            return;
        }

        var sheet = await _repository.GetCurrentRateSheetAsync(cancellationToken);
        if (sheet is null)
        {
            return;
        }

        var updatedAt = await _repository.GetCurrentPublishedAtAsync(cancellationToken)
                        ?? FormatUtc(DateTimeOffset.UtcNow);
        await _publisher.PublishRatesAsync(BuildMessage(version.Value, sheet, updatedAt), cancellationToken);
    }

    public async Task<int> UpdateRatesAsync(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> newRates,
        CancellationToken cancellationToken = default)
    {
        var currentRules = await _repository.GetCurrentInsertCoinRulesAsync(cancellationToken)
                           ?? RateRepository.DefaultInsertCoinRules;
        return await UpdateRateSheetAsync(new RateSheet(newRates, currentRules), cancellationToken);
    }

    public async Task<int> UpdateRateSheetAsync(
        RateSheet rateSheet,
        CancellationToken cancellationToken = default)
    {
        ValidateRateSheet(rateSheet);

        var publishedAt = FormatUtc(DateTimeOffset.UtcNow);
        var version = await _repository.PublishNewVersionAsync(rateSheet, publishedAt, cancellationToken);
        await _publisher.PublishRatesAsync(BuildMessage(version, rateSheet, publishedAt), cancellationToken);
        await TryLogRatePublishedAsync(version, rateSheet, publishedAt, cancellationToken);
        return version;
    }

    private async Task TryLogRatePublishedAsync(
        int version,
        RateSheet rateSheet,
        string publishedAt,
        CancellationToken cancellationToken)
    {
        if (_eventLogger is null)
        {
            return;
        }

        // Embed the full rate sheet in the payload so the audit row is
        // self-contained — a future investigator never has to join against
        // the rates table (which may have been rolled forward by then) to
        // know what was in effect at the moment of publish.
        var payload = JsonSerializer.Serialize(
            new
            {
                version,
                publishedAt,
                addTimeRates = rateSheet.AddTimeRates,
                insertCoinRules = rateSheet.InsertCoinRules
            },
            JsonOptions);

        await _eventLogger.AppendAsync(
            new EventLogAppendRequest(
                Ts: FormatUtc(DateTimeOffset.UtcNow),
                EventType: EventTypes.RatePublished,
                StationId: null,
                AcceptorId: null,
                AmountPesos: null,
                SecondsCredited: null,
                Payload: payload),
            cancellationToken);
    }

    private static void ValidateRateSheet(RateSheet sheet)
    {
        if (sheet is null)
        {
            throw new ArgumentNullException(nameof(sheet));
        }

        ValidateDeviceMap(
            sheet.AddTimeRates,
            SupportedAddTimeDenominations,
            "add-time rate",
            nameof(sheet));
        ValidateInsertCoinRules(sheet.InsertCoinRules);
    }

    private static void ValidateDeviceMap(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> rates,
        IReadOnlyList<int> requiredKeys,
        string label,
        string paramName)
    {
        if (rates is null)
        {
            throw new ArgumentNullException(paramName);
        }

        foreach (var device in SupportedDeviceTypes)
        {
            if (!rates.TryGetValue(device, out var perKey))
            {
                throw new ArgumentException(
                    $"{label} sheet missing device type '{device}'.",
                    paramName);
            }

            foreach (var key in requiredKeys)
            {
                if (!perKey.TryGetValue(key, out var seconds))
                {
                    throw new ArgumentException(
                        $"{label} sheet missing {device}/\u20b1{key}.",
                        paramName);
                }

                if (seconds <= 0)
                {
                    throw new ArgumentException(
                        $"{label} {device}/\u20b1{key} must be > 0; got {seconds}.",
                        paramName);
                }
            }
        }
    }

    private static RateConfigMessage BuildMessage(int version, RateSheet sheet, string updatedAt)
    {
        var addTimeWire = ToWireMap(sheet.AddTimeRates);
        var insertCoinWire = ToRuleWireMap(sheet.InsertCoinRules);

        return new RateConfigMessage
        {
            Version = version,
            Rates = addTimeWire,
            AddTimeRates = addTimeWire,
            InsertCoinRules = insertCoinWire,
            UpdatedAt = updatedAt
        };
    }

    private static void ValidateInsertCoinRules(
        IReadOnlyDictionary<string, InsertCoinRateRule> rules)
    {
        if (rules is null)
        {
            throw new ArgumentNullException(nameof(rules));
        }

        foreach (var device in SupportedDeviceTypes)
        {
            if (!rules.TryGetValue(device, out var rule))
            {
                throw new ArgumentException(
                    $"insert-coin rule sheet missing device type '{device}'.",
                    nameof(rules));
            }

            if (rule.StarterMaxPesos <= 0)
            {
                throw new ArgumentException($"{device} starter max pesos must be > 0.", nameof(rules));
            }

            if (rule.StarterSeconds <= 0)
            {
                throw new ArgumentException($"{device} starter seconds must be > 0.", nameof(rules));
            }

            if (rule.NormalizedFromPesos <= rule.StarterMaxPesos)
            {
                throw new ArgumentException(
                    $"{device} normalized-from pesos must be greater than starter max pesos.",
                    nameof(rules));
            }

            if (rule.PerPesoSeconds <= 0)
            {
                throw new ArgumentException($"{device} per-peso seconds must be > 0.", nameof(rules));
            }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ToWireMap(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> rates)
    {
        var wire = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (device, perKey) in rates)
        {
            var perKeyString = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, seconds) in perKey)
            {
                perKeyString[key.ToString(CultureInfo.InvariantCulture)] = seconds;
            }
            wire[device] = perKeyString;
        }

        return wire;
    }

    private static IReadOnlyDictionary<string, InsertCoinRateRuleMessage> ToRuleWireMap(
        IReadOnlyDictionary<string, InsertCoinRateRule> rules)
    {
        var wire = new Dictionary<string, InsertCoinRateRuleMessage>(StringComparer.Ordinal);
        foreach (var (device, rule) in rules)
        {
            wire[device] = new InsertCoinRateRuleMessage
            {
                StarterMaxPesos = rule.StarterMaxPesos,
                StarterSeconds = rule.StarterSeconds,
                NormalizedFromPesos = rule.NormalizedFromPesos,
                PerPesoSeconds = rule.PerPesoSeconds
            };
        }

        return wire;
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
