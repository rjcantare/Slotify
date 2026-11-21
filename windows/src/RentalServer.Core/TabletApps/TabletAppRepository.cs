using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.TabletApps;

public sealed class TabletAppRepository
{
    public static readonly IReadOnlyList<TabletAppDefinition> DefaultApps =
    [
        new("YouTube", "com.google.android.youtube", Enabled: true, DeepFreeze: true, SortOrder: 0, Category: TabletAppCategories.VideoSocial),
        new("TikTok", "com.zhiliaoapp.musically", Enabled: true, DeepFreeze: true, SortOrder: 1, Category: TabletAppCategories.VideoSocial),
        new("Roblox", "com.roblox.client", Enabled: true, DeepFreeze: true, SortOrder: 2, Category: TabletAppCategories.Games),
        new("Mobile Legends", "com.mobile.legends", Enabled: true, DeepFreeze: true, SortOrder: 3, Category: TabletAppCategories.Games),
        new("COD Mobile", "com.activision.callofduty.shooter", Enabled: true, DeepFreeze: true, SortOrder: 4, Category: TabletAppCategories.Games)
    ];

    private readonly string _databasePath;

    public TabletAppRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS tablet_app_config_versions (
                  version      INTEGER PRIMARY KEY,
                  published_at TEXT    NOT NULL,
                  is_current   INTEGER NOT NULL DEFAULT 0
                );
                """;
            await versionTable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var currentIndex = connection.CreateCommand())
        {
            currentIndex.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_tablet_app_config_current
                  ON tablet_app_config_versions(is_current) WHERE is_current = 1;
                """;
            await currentIndex.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var appsTable = connection.CreateCommand())
        {
            appsTable.CommandText =
                """
                CREATE TABLE IF NOT EXISTS tablet_apps (
                  version      INTEGER NOT NULL REFERENCES tablet_app_config_versions(version),
                  sort_order   INTEGER NOT NULL CHECK (sort_order >= 0),
                  display_name TEXT    NOT NULL CHECK (length(trim(display_name)) > 0),
                  package_name TEXT    NOT NULL CHECK (length(trim(package_name)) > 0),
                  is_enabled   INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
                  deep_freeze  INTEGER NOT NULL CHECK (deep_freeze IN (0, 1)),
                  category     TEXT    NOT NULL DEFAULT 'other',
                  PRIMARY KEY (version, package_name)
                );
                """;
            await appsTable.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureCategoryColumnAsync(connection, cancellationToken);
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

        await InsertVersionAsync(connection, transaction, version: 1, publishedAt, isCurrent: true, cancellationToken);
        await InsertAppsAsync(connection, transaction, version: 1, DefaultApps, cancellationToken);
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

        command.CommandText = "SELECT published_at FROM tablet_app_config_versions WHERE is_current = 1 LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task<TabletAppConfig?> GetCurrentConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        var version = await GetCurrentVersionAsync(connection, transaction: null, cancellationToken);
        return version is null
            ? null
            : new TabletAppConfig(await ReadAppsForVersionAsync(connection, transaction: null, version.Value, cancellationToken));
    }

    public async Task<int> PublishNewVersionAsync(
        TabletAppConfig config,
        string publishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var nextVersion = await PublishNewVersionCoreAsync(
            connection,
            transaction,
            config,
            publishedAt,
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
        command.CommandText = "SELECT version FROM tablet_app_config_versions WHERE is_current = 1 LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<int> PublishNewVersionCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TabletAppConfig config,
        string publishedAt,
        CancellationToken cancellationToken)
    {
        int nextVersion;
        await using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText = "SELECT COALESCE(MAX(version), 0) FROM tablet_app_config_versions;";
            nextVersion = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken) ?? 0) + 1;
        }

        await using (var unflag = connection.CreateCommand())
        {
            unflag.Transaction = transaction;
            unflag.CommandText = "UPDATE tablet_app_config_versions SET is_current = 0 WHERE is_current = 1;";
            await unflag.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertVersionAsync(connection, transaction, nextVersion, publishedAt, isCurrent: true, cancellationToken);
        await InsertAppsAsync(connection, transaction, nextVersion, config.Apps, cancellationToken);
        return nextVersion;
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string publishedAt,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tablet_app_config_versions (version, published_at, is_current)
            VALUES ($version, $published_at, $is_current);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$published_at", publishedAt);
        command.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAppsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        IReadOnlyList<TabletAppDefinition> apps,
        CancellationToken cancellationToken)
    {
        foreach (var app in apps)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO tablet_apps (
                  version,
                  sort_order,
                  display_name,
                  package_name,
                  is_enabled,
                  deep_freeze,
                  category
                )
                VALUES (
                  $version,
                  $sort_order,
                  $display_name,
                  $package_name,
                  $is_enabled,
                  $deep_freeze,
                  $category
                );
                """;
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$sort_order", app.SortOrder);
            command.Parameters.AddWithValue("$display_name", app.DisplayName);
            command.Parameters.AddWithValue("$package_name", app.PackageName);
            command.Parameters.AddWithValue("$is_enabled", app.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$deep_freeze", app.DeepFreeze ? 1 : 0);
            command.Parameters.AddWithValue("$category", TabletAppCategories.Normalize(app.Category));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<TabletAppDefinition>> ReadAppsForVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT display_name, package_name, is_enabled, deep_freeze, sort_order, category
            FROM tablet_apps
            WHERE version = $version
            ORDER BY sort_order, display_name;
            """;
        command.Parameters.AddWithValue("$version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var apps = new List<TabletAppDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            apps.Add(new TabletAppDefinition(
                DisplayName: reader.GetString(0),
                PackageName: reader.GetString(1),
                Enabled: reader.GetInt32(2) == 1,
                DeepFreeze: reader.GetInt32(3) == 1,
                SortOrder: reader.GetInt32(4),
                Category: TabletAppCategories.Normalize(reader.GetString(5))));
        }

        return apps;
    }

    private static async Task EnsureCategoryColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(tablet_apps);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "category", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE tablet_apps ADD COLUMN category TEXT NOT NULL DEFAULT 'other';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
