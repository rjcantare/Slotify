using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.TabletAdmin;

public sealed class TabletAdminRepository
{
    public const string DefaultPassword = "admin@123!";

    private readonly string _databasePath;

    public TabletAdminRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS tablet_admin_config_versions (
                  version       INTEGER PRIMARY KEY,
                  published_at  TEXT    NOT NULL,
                  algorithm     TEXT    NOT NULL,
                  password_salt TEXT    NOT NULL,
                  password_hash TEXT    NOT NULL,
                  is_current    INTEGER NOT NULL DEFAULT 0
                );
                """;
            await table.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var currentIndex = connection.CreateCommand())
        {
            currentIndex.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_tablet_admin_config_current
                  ON tablet_admin_config_versions(is_current) WHERE is_current = 1;
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

        var hash = TabletAdminPasswordHasher.HashNewPassword(DefaultPassword);
        await InsertVersionAsync(
            connection,
            transaction,
            version: 1,
            publishedAt,
            TabletAdminPasswordHasher.Algorithm,
            hash.SaltBase64,
            hash.HashBase64,
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

        command.CommandText = "SELECT published_at FROM tablet_admin_config_versions WHERE is_current = 1 LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task<TabletAdminConfig?> GetCurrentConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT algorithm, password_salt, password_hash
            FROM tablet_admin_config_versions
            WHERE is_current = 1
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TabletAdminConfig(
            Algorithm: reader.GetString(0),
            PasswordSalt: reader.GetString(1),
            PasswordHash: reader.GetString(2));
    }

    public async Task<int> PublishNewPasswordAsync(
        string password,
        string publishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int nextVersion;
        await using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(version), 0) FROM tablet_admin_config_versions;";
            nextVersion = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken) ?? 0) + 1;
        }

        await using (var unflag = connection.CreateCommand())
        {
            unflag.Transaction = transaction;
            unflag.CommandText = "UPDATE tablet_admin_config_versions SET is_current = 0 WHERE is_current = 1;";
            await unflag.ExecuteNonQueryAsync(cancellationToken);
        }

        var hash = TabletAdminPasswordHasher.HashNewPassword(password);
        await InsertVersionAsync(
            connection,
            transaction,
            nextVersion,
            publishedAt,
            TabletAdminPasswordHasher.Algorithm,
            hash.SaltBase64,
            hash.HashBase64,
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
        command.CommandText = "SELECT version FROM tablet_admin_config_versions WHERE is_current = 1 LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string publishedAt,
        string algorithm,
        string passwordSalt,
        string passwordHash,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tablet_admin_config_versions (
              version,
              published_at,
              algorithm,
              password_salt,
              password_hash,
              is_current
            )
            VALUES (
              $version,
              $published_at,
              $algorithm,
              $password_salt,
              $password_hash,
              $is_current
            );
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$published_at", publishedAt);
        command.Parameters.AddWithValue("$algorithm", algorithm);
        command.Parameters.AddWithValue("$password_salt", passwordSalt);
        command.Parameters.AddWithValue("$password_hash", passwordHash);
        command.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
