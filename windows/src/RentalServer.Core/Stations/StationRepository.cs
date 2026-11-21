using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Stations;

public sealed class StationRepository
{
    private readonly string _databasePath;

    public StationRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS stations (
              station_id        TEXT    PRIMARY KEY,
              device_type       TEXT    NOT NULL CHECK (device_type IN ('pc', 'phn', 'tab')),
              display_name      TEXT,
              created_at        TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
              last_seen_at      TEXT,
              app_version       TEXT,
              is_online         INTEGER NOT NULL DEFAULT 0,
              last_credit_seqno INTEGER NOT NULL DEFAULT 0
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureHiddenColumnAsync(connection, cancellationToken);
    }

    public async Task UpsertOnlineAsync(
        string stationId,
        string deviceType,
        string? appVersion,
        string lastSeenAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO stations (
              station_id,
              device_type,
              last_seen_at,
              app_version,
              is_online,
              is_hidden
            )
            VALUES (
              $station_id,
              $device_type,
              $last_seen_at,
              $app_version,
              1,
              0
            )
            ON CONFLICT(station_id) DO UPDATE SET
              device_type = excluded.device_type,
              last_seen_at = excluded.last_seen_at,
              app_version = excluded.app_version,
              is_online = excluded.is_online,
              is_hidden = 0;
            """;

        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$device_type", deviceType);
        command.Parameters.AddWithValue("$last_seen_at", lastSeenAt);
        command.Parameters.AddWithValue("$app_version", (object?)appVersion ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOfflineAsync(
        string stationId,
        string lastSeenAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE stations
            SET
              last_seen_at = $last_seen_at,
              is_online = 0
            WHERE station_id = $station_id;
            """;

        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$last_seen_at", lastSeenAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StationRecord?> GetAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              station_id,
              device_type,
              display_name,
              last_seen_at,
              app_version,
              is_online
            FROM stations
            WHERE station_id = $station_id
              AND is_hidden = 0;
            """;

        command.Parameters.AddWithValue("$station_id", stationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5) == 1);
    }

    public async Task<IReadOnlyList<StationRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              station_id,
              device_type,
              display_name,
              last_seen_at,
              app_version,
              is_online
            FROM stations
            WHERE is_hidden = 0
            ORDER BY station_id;
            """;

        var stations = new List<StationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            stations.Add(new StationRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1));
        }

        return stations;
    }

    public async Task<int> HideOfflineAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE stations
            SET is_hidden = 1,
                is_online = 0
            WHERE station_id = $station_id
              AND is_online = 0;
            """;

        command.Parameters.AddWithValue("$station_id", stationId);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureHiddenColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasHiddenColumn = false;

        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(stations);";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1).Equals("is_hidden", StringComparison.OrdinalIgnoreCase))
                {
                    hasHiddenColumn = true;
                    break;
                }
            }
        }

        if (hasHiddenColumn)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText =
            """
            ALTER TABLE stations
            ADD COLUMN is_hidden INTEGER NOT NULL DEFAULT 0;
            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
