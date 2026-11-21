using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Abuse;

public sealed class AbusePreventionRepository
{
    private readonly string _databasePath;

    public AbusePreventionRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using (var table = connection.CreateCommand())
        {
            table.CommandText =
                """
                CREATE TABLE IF NOT EXISTS abuse_prevention_config_versions (
                  version             INTEGER PRIMARY KEY,
                  published_at         TEXT    NOT NULL,
                  enabled              INTEGER NOT NULL DEFAULT 0,
                  max_empty_claims     INTEGER NOT NULL DEFAULT 3,
                  ban_minutes          INTEGER NOT NULL DEFAULT 2,
                  is_current           INTEGER NOT NULL DEFAULT 0
                );
                """;
            await table.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var currentIndex = connection.CreateCommand())
        {
            currentIndex.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_abuse_prevention_config_current
                  ON abuse_prevention_config_versions(is_current) WHERE is_current = 1;
                """;
            await currentIndex.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task EnsureSeededAsync(string publishedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (await GetCurrentVersionAsync(connection, transaction, cancellationToken) is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await InsertVersionAsync(
            connection,
            transaction,
            version: 1,
            publishedAt,
            AbusePreventionService.DefaultConfig,
            isCurrent: true,
            cancellationToken);
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

        command.CommandText = "SELECT published_at FROM abuse_prevention_config_versions WHERE is_current = 1 LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task<AbusePreventionService.Config?> GetCurrentConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              enabled,
              max_empty_claims,
              ban_minutes
            FROM abuse_prevention_config_versions
            WHERE is_current = 1
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AbusePreventionService.Config(
            Enabled: reader.GetInt32(0) == 1,
            MaxEmptyClaims: reader.GetInt32(1),
            BanMinutes: reader.GetInt32(2));
    }

    public async Task<int> PublishNewVersionAsync(
        AbusePreventionService.Config config,
        string publishedAt,
        CancellationToken cancellationToken = default)
    {
        AbusePreventionService.ValidateConfig(config);

        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int nextVersion;
        await using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(version), 0) FROM abuse_prevention_config_versions;";
            nextVersion = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken) ?? 0) + 1;
        }

        await using (var unflag = connection.CreateCommand())
        {
            unflag.Transaction = transaction;
            unflag.CommandText = "UPDATE abuse_prevention_config_versions SET is_current = 0 WHERE is_current = 1;";
            await unflag.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertVersionAsync(
            connection,
            transaction,
            nextVersion,
            publishedAt,
            config,
            isCurrent: true,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return nextVersion;
    }

    private static async Task<int?> GetCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM abuse_prevention_config_versions WHERE is_current = 1 LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string publishedAt,
        AbusePreventionService.Config config,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO abuse_prevention_config_versions (
              version,
              published_at,
              enabled,
              max_empty_claims,
              ban_minutes,
              is_current
            )
            VALUES (
              $version,
              $published_at,
              $enabled,
              $max_empty_claims,
              $ban_minutes,
              $is_current
            );
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$published_at", publishedAt);
        command.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$max_empty_claims", config.MaxEmptyClaims);
        command.Parameters.AddWithValue("$ban_minutes", config.BanMinutes);
        command.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
