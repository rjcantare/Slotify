using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.PcPolicy;

public sealed class PcPolicyRepository
{
    private readonly string _databasePath;

    public PcPolicyRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS pc_policy_config_versions (
                  version                      INTEGER PRIMARY KEY,
                  published_at                 TEXT    NOT NULL,
                  auto_close_on_lock_enabled   INTEGER NOT NULL DEFAULT 0,
                  idle_shutdown_enabled        INTEGER NOT NULL DEFAULT 0,
                  idle_shutdown_minutes        INTEGER NOT NULL DEFAULT 5,
                  is_current                   INTEGER NOT NULL DEFAULT 0
                );
                """;
            await table.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var currentIndex = connection.CreateCommand())
        {
            currentIndex.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_pc_policy_config_current
                  ON pc_policy_config_versions(is_current) WHERE is_current = 1;
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
            PcPolicyConfig.Default,
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

        command.CommandText = "SELECT published_at FROM pc_policy_config_versions WHERE is_current = 1 LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task<PcPolicyConfig?> GetCurrentConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              auto_close_on_lock_enabled,
              idle_shutdown_enabled,
              idle_shutdown_minutes
            FROM pc_policy_config_versions
            WHERE is_current = 1
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PcPolicyConfig(
            AutoCloseOnLockEnabled: reader.GetInt32(0) == 1,
            IdleShutdownEnabled: reader.GetInt32(1) == 1,
            IdleShutdownMinutes: reader.GetInt32(2));
    }

    public async Task<int> PublishNewVersionAsync(
        PcPolicyConfig config,
        string publishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int nextVersion;
        await using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(version), 0) FROM pc_policy_config_versions;";
            nextVersion = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken) ?? 0) + 1;
        }

        await using (var unflag = connection.CreateCommand())
        {
            unflag.Transaction = transaction;
            unflag.CommandText = "UPDATE pc_policy_config_versions SET is_current = 0 WHERE is_current = 1;";
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
        command.CommandText = "SELECT version FROM pc_policy_config_versions WHERE is_current = 1 LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string publishedAt,
        PcPolicyConfig config,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO pc_policy_config_versions (
              version,
              published_at,
              auto_close_on_lock_enabled,
              idle_shutdown_enabled,
              idle_shutdown_minutes,
              is_current
            )
            VALUES (
              $version,
              $published_at,
              $auto_close_on_lock_enabled,
              $idle_shutdown_enabled,
              $idle_shutdown_minutes,
              $is_current
            );
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$published_at", publishedAt);
        command.Parameters.AddWithValue("$auto_close_on_lock_enabled", config.AutoCloseOnLockEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$idle_shutdown_enabled", config.IdleShutdownEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$idle_shutdown_minutes", config.IdleShutdownMinutes);
        command.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
