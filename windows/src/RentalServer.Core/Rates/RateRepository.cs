using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Rates;

public sealed class RateRepository
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> LegacyDefaultRates =
        new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["pc"]  = new Dictionary<int, int> { [1] = 60,  [5] = 360, [10] = 750,  [20] = 1800 },
            ["phn"] = new Dictionary<int, int> { [1] = 120, [5] = 720, [10] = 1500, [20] = 3600 },
            ["tab"] = new Dictionary<int, int> { [1] = 90,  [5] = 540, [10] = 1125, [20] = 2700 }
        };

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> DefaultAddTimeRates =
        new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["pc"]  = new Dictionary<int, int> { [1] = 360, [5] = 1800, [10] = 3600, [20] = 7200 },
            ["phn"] = new Dictionary<int, int> { [1] = 120, [5] = 720,  [10] = 1500, [20] = 3600 },
            ["tab"] = new Dictionary<int, int> { [1] = 90,  [5] = 540,  [10] = 1125, [20] = 2700 }
        };

    public static readonly IReadOnlyDictionary<string, InsertCoinRateRule> DefaultInsertCoinRules =
        new Dictionary<string, InsertCoinRateRule>(StringComparer.Ordinal)
        {
            ["pc"] = new(StarterMaxPesos: 7, StarterSeconds: 1800, NormalizedFromPesos: 10, PerPesoSeconds: 360),
            ["phn"] = new(StarterMaxPesos: 7, StarterSeconds: 1800, NormalizedFromPesos: 10, PerPesoSeconds: 360),
            ["tab"] = new(StarterMaxPesos: 7, StarterSeconds: 1800, NormalizedFromPesos: 10, PerPesoSeconds: 360)
        };

    private readonly string _databasePath;

    public RateRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using (var versionTable = connection.CreateCommand())
        {
            versionTable.CommandText =
                """
                CREATE TABLE IF NOT EXISTS rate_versions (
                  version      INTEGER PRIMARY KEY,
                  published_at TEXT    NOT NULL,
                  is_current   INTEGER NOT NULL DEFAULT 0
                );
                """;
            await versionTable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var versionIndex = connection.CreateCommand())
        {
            versionIndex.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_rate_versions_current
                  ON rate_versions(is_current) WHERE is_current = 1;
                """;
            await versionIndex.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ratesTable = connection.CreateCommand())
        {
            ratesTable.CommandText =
                """
                CREATE TABLE IF NOT EXISTS rates (
                  version      INTEGER NOT NULL REFERENCES rate_versions(version),
                  device_type  TEXT    NOT NULL CHECK (device_type IN ('pc', 'phn', 'tab')),
                  denomination INTEGER NOT NULL CHECK (denomination IN (1, 5, 10, 20)),
                  seconds      INTEGER NOT NULL CHECK (seconds > 0),
                  PRIMARY KEY (version, device_type, denomination)
                );
                """;
            await ratesTable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var rulesTable = connection.CreateCommand())
        {
            rulesTable.CommandText =
                """
                CREATE TABLE IF NOT EXISTS insert_coin_rules (
                  version                INTEGER NOT NULL REFERENCES rate_versions(version),
                  device_type            TEXT    NOT NULL CHECK (device_type IN ('pc', 'phn', 'tab')),
                  starter_max_pesos      INTEGER NOT NULL CHECK (starter_max_pesos > 0),
                  starter_seconds        INTEGER NOT NULL CHECK (starter_seconds > 0),
                  normalized_from_pesos  INTEGER NOT NULL CHECK (normalized_from_pesos > starter_max_pesos),
                  per_peso_seconds       INTEGER NOT NULL CHECK (per_peso_seconds > 0),
                  PRIMARY KEY (version, device_type)
                );
                """;
            await rulesTable.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task EnsureSeededAsync(string publishedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(connection, transaction, cancellationToken);
        if (currentVersion is not null)
        {
            var currentRates = await ReadRatesForVersionAsync(connection, transaction, currentVersion.Value, cancellationToken);
            if (RatesMatch(currentRates, LegacyDefaultRates))
            {
                await PublishNewVersionCoreAsync(
                    connection,
                    transaction,
                    new RateSheet(DefaultAddTimeRates, DefaultInsertCoinRules),
                    publishedAt,
                    cancellationToken);
            }
            else
            {
                await EnsureInsertCoinRulesForVersionAsync(connection, transaction, currentVersion.Value, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await InsertVersionAsync(connection, transaction, version: 1, publishedAt, isCurrent: true, cancellationToken);
        await InsertRatesAsync(connection, transaction, version: 1, DefaultAddTimeRates, cancellationToken);
        await InsertInsertCoinRulesAsync(connection, transaction, version: 1, DefaultInsertCoinRules, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int?> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        return await GetCurrentVersionAsync(connection, transaction: null, cancellationToken);
    }

    public async Task<string?> GetCurrentPublishedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT published_at FROM rate_versions WHERE is_current = 1 LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>?> GetCurrentRatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var version = await GetCurrentVersionAsync(connection, transaction: null, cancellationToken);
        return version is null
            ? null
            : await ReadRatesForVersionAsync(connection, transaction: null, version.Value, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, InsertCoinRateRule>?> GetCurrentInsertCoinRulesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var version = await GetCurrentVersionAsync(connection, transaction: null, cancellationToken);
        return version is null
            ? null
            : await ReadInsertCoinRulesForVersionAsync(connection, transaction: null, version.Value, cancellationToken);
    }

    public async Task<RateSheet?> GetCurrentRateSheetAsync(CancellationToken cancellationToken = default)
    {
        var rates = await GetCurrentRatesAsync(cancellationToken);
        if (rates is null)
        {
            return null;
        }

        var rules = await GetCurrentInsertCoinRulesAsync(cancellationToken)
                    ?? DefaultInsertCoinRules;
        return new RateSheet(rates, rules);
    }

    public Task<int> PublishNewVersionAsync(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> newRates,
        string publishedAt,
        CancellationToken cancellationToken = default)
    {
        return PublishNewVersionAsync(
            new RateSheet(newRates, DefaultInsertCoinRules),
            publishedAt,
            cancellationToken);
    }

    public async Task<int> PublishNewVersionAsync(
        RateSheet rateSheet,
        string publishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var nextVersion = await PublishNewVersionCoreAsync(
            connection,
            transaction,
            rateSheet,
            publishedAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return nextVersion;
    }

    public async Task<int?> GetSecondsAsync(
        string deviceType,
        int denomination,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT r.seconds
            FROM rates r
            JOIN rate_versions v ON v.version = r.version
            WHERE v.is_current = 1
              AND r.device_type = $device_type
              AND r.denomination = $denomination;
            """;

        command.Parameters.AddWithValue("$device_type", deviceType);
        command.Parameters.AddWithValue("$denomination", denomination);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    public async Task<int?> GetAddTimeSecondsForTotalAsync(
        string deviceType,
        int totalPesos,
        CancellationToken cancellationToken = default)
    {
        if (totalPesos <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalPesos));
        }

        var rates = await GetCurrentRatesAsync(cancellationToken);
        if (rates is null || !rates.TryGetValue(deviceType, out var perDenom))
        {
            return null;
        }

        var best = new int?[totalPesos + 1];
        best[0] = 0;

        for (var pesos = 1; pesos <= totalPesos; pesos++)
        {
            foreach (var (denom, seconds) in perDenom)
            {
                if (denom > pesos || best[pesos - denom] is not int previous)
                {
                    continue;
                }

                var candidate = previous + seconds;
                if (best[pesos] is null || candidate > best[pesos])
                {
                    best[pesos] = candidate;
                }
            }
        }

        return best[totalPesos];
    }

    public async Task<int?> GetInsertCoinSecondsForTotalAsync(
        string deviceType,
        int totalPesos,
        CancellationToken cancellationToken = default)
    {
        if (totalPesos < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalPesos));
        }

        var rules = await GetCurrentInsertCoinRulesAsync(cancellationToken);
        if (rules is null || !rules.TryGetValue(deviceType, out var rule))
        {
            return null;
        }

        return rule.CalculateSeconds(totalPesos);
    }

    private static async Task<int?> GetCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM rate_versions WHERE is_current = 1 LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<int> PublishNewVersionCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RateSheet rateSheet,
        string publishedAt,
        CancellationToken cancellationToken)
    {
        int nextVersion;
        await using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(version), 0) FROM rate_versions;";
            nextVersion = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken) ?? 0) + 1;
        }

        await using (var unflag = connection.CreateCommand())
        {
            unflag.Transaction = transaction;
            unflag.CommandText = "UPDATE rate_versions SET is_current = 0 WHERE is_current = 1;";
            await unflag.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertVersionAsync(connection, transaction, nextVersion, publishedAt, isCurrent: true, cancellationToken);
        await InsertRatesAsync(connection, transaction, nextVersion, rateSheet.AddTimeRates, cancellationToken);
        await InsertInsertCoinRulesAsync(connection, transaction, nextVersion, rateSheet.InsertCoinRules, cancellationToken);
        return nextVersion;
    }

    private static async Task EnsureInsertCoinRulesForVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM insert_coin_rules WHERE version = $version;";
        countCommand.Parameters.AddWithValue("$version", version);

        var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (count > 0)
        {
            return;
        }

        await InsertInsertCoinRulesAsync(connection, transaction, version, DefaultInsertCoinRules, cancellationToken);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string publishedAt,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        await using var insertVersion = connection.CreateCommand();
        insertVersion.Transaction = transaction;
        insertVersion.CommandText =
            """
            INSERT INTO rate_versions (version, published_at, is_current)
            VALUES ($version, $published_at, $is_current);
            """;
        insertVersion.Parameters.AddWithValue("$version", version);
        insertVersion.Parameters.AddWithValue("$published_at", publishedAt);
        insertVersion.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
        await insertVersion.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> rates,
        CancellationToken cancellationToken)
    {
        foreach (var (deviceType, perDenom) in rates)
        {
            foreach (var (denom, seconds) in perDenom)
            {
                await using var insertRate = connection.CreateCommand();
                insertRate.Transaction = transaction;
                insertRate.CommandText =
                    """
                    INSERT INTO rates (version, device_type, denomination, seconds)
                    VALUES ($version, $device_type, $denomination, $seconds);
                    """;
                insertRate.Parameters.AddWithValue("$version", version);
                insertRate.Parameters.AddWithValue("$device_type", deviceType);
                insertRate.Parameters.AddWithValue("$denomination", denom);
                insertRate.Parameters.AddWithValue("$seconds", seconds);
                await insertRate.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task InsertInsertCoinRulesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        IReadOnlyDictionary<string, InsertCoinRateRule> rules,
        CancellationToken cancellationToken)
    {
        foreach (var (deviceType, rule) in rules)
        {
            await using var insertRule = connection.CreateCommand();
            insertRule.Transaction = transaction;
            insertRule.CommandText =
                """
                INSERT INTO insert_coin_rules (
                  version,
                  device_type,
                  starter_max_pesos,
                  starter_seconds,
                  normalized_from_pesos,
                  per_peso_seconds
                )
                VALUES (
                  $version,
                  $device_type,
                  $starter_max_pesos,
                  $starter_seconds,
                  $normalized_from_pesos,
                  $per_peso_seconds
                );
                """;
            insertRule.Parameters.AddWithValue("$version", version);
            insertRule.Parameters.AddWithValue("$device_type", deviceType);
            insertRule.Parameters.AddWithValue("$starter_max_pesos", rule.StarterMaxPesos);
            insertRule.Parameters.AddWithValue("$starter_seconds", rule.StarterSeconds);
            insertRule.Parameters.AddWithValue("$normalized_from_pesos", rule.NormalizedFromPesos);
            insertRule.Parameters.AddWithValue("$per_peso_seconds", rule.PerPesoSeconds);
            await insertRule.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>> ReadRatesForVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT device_type, denomination, seconds
            FROM rates
            WHERE version = $version;
            """;
        command.Parameters.AddWithValue("$version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var perDevice = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var device = reader.GetString(0);
            var denomination = reader.GetInt32(1);
            var seconds = reader.GetInt32(2);

            if (!perDevice.TryGetValue(device, out var perDenom))
            {
                perDenom = new Dictionary<int, int>();
                perDevice[device] = perDenom;
            }

            perDenom[denomination] = seconds;
        }

        var result = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal);
        foreach (var (device, perDenom) in perDevice)
        {
            result[device] = perDenom;
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, InsertCoinRateRule>> ReadInsertCoinRulesForVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
              device_type,
              starter_max_pesos,
              starter_seconds,
              normalized_from_pesos,
              per_peso_seconds
            FROM insert_coin_rules
            WHERE version = $version;
            """;
        command.Parameters.AddWithValue("$version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, InsertCoinRateRule>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = new InsertCoinRateRule(
                StarterMaxPesos: reader.GetInt32(1),
                StarterSeconds: reader.GetInt32(2),
                NormalizedFromPesos: reader.GetInt32(3),
                PerPesoSeconds: reader.GetInt32(4));
        }

        return result;
    }

    private static bool RatesMatch(
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> actual,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> expected)
    {
        foreach (var (device, expectedPerDenom) in expected)
        {
            if (!actual.TryGetValue(device, out var actualPerDenom))
            {
                return false;
            }

            foreach (var (denom, expectedSeconds) in expectedPerDenom)
            {
                if (!actualPerDenom.TryGetValue(denom, out var actualSeconds) ||
                    actualSeconds != expectedSeconds)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
