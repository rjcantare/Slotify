using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Sessions;

public sealed class SessionRepository
{
    private readonly string _databasePath;

    public SessionRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS sessions (
                  session_id        TEXT    PRIMARY KEY,
                  station_id        TEXT    NOT NULL REFERENCES stations(station_id),
                  started_at        TEXT    NOT NULL,
                  ended_at          TEXT,
                  remaining_sec     INTEGER NOT NULL DEFAULT 0,
                  is_locked         INTEGER NOT NULL DEFAULT 1,
                  last_credit_seqno INTEGER NOT NULL DEFAULT 0,
                  updated_at        TEXT    NOT NULL
                );
                """;

            await tableCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_active
                  ON sessions(station_id) WHERE ended_at IS NULL;
                """;

            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertAsync(SessionRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        // INSERT path stamps the immutable fields (session_id, station_id, started_at).
        // UPDATE path only touches the fields that change at a transition. Persisted
        // strictly on start/credit/lock/end transitions — never on every tick.
        command.CommandText =
            """
            INSERT INTO sessions (
              session_id,
              station_id,
              started_at,
              ended_at,
              remaining_sec,
              is_locked,
              last_credit_seqno,
              updated_at
            )
            VALUES (
              $session_id,
              $station_id,
              $started_at,
              $ended_at,
              $remaining_sec,
              $is_locked,
              $last_credit_seqno,
              $updated_at
            )
            ON CONFLICT(session_id) DO UPDATE SET
              ended_at = excluded.ended_at,
              remaining_sec = excluded.remaining_sec,
              is_locked = excluded.is_locked,
              last_credit_seqno = excluded.last_credit_seqno,
              updated_at = excluded.updated_at;
            """;

        command.Parameters.AddWithValue("$session_id", record.SessionId);
        command.Parameters.AddWithValue("$station_id", record.StationId);
        command.Parameters.AddWithValue("$started_at", record.StartedAt);
        command.Parameters.AddWithValue("$ended_at", (object?)record.EndedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$remaining_sec", record.RemainingSeconds);
        command.Parameters.AddWithValue("$is_locked", record.IsLocked ? 1 : 0);
        command.Parameters.AddWithValue("$last_credit_seqno", record.LastCreditSeqNo);
        command.Parameters.AddWithValue("$updated_at", record.UpdatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SessionRecord?> GetActiveForStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              session_id, station_id, started_at, ended_at,
              remaining_sec, is_locked, last_credit_seqno, updated_at
            FROM sessions
            WHERE station_id = $station_id AND ended_at IS NULL
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$station_id", stationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5) == 1,
            reader.GetInt64(6),
            reader.GetString(7));
    }

    public async Task<IReadOnlyList<SessionRecord>> LoadActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              session_id,
              station_id,
              started_at,
              ended_at,
              remaining_sec,
              is_locked,
              last_credit_seqno,
              updated_at
            FROM sessions
            WHERE ended_at IS NULL
            ORDER BY started_at;
            """;

        var sessions = new List<SessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5) == 1,
                reader.GetInt64(6),
                reader.GetString(7)));
        }

        return sessions;
    }
}
