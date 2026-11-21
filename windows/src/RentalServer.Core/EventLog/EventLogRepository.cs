using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.EventLog;

public sealed class EventLogRepository : IEventLogger
{
    private readonly string _databasePath;

    // Append must be serialized: prev_hash is read inside the same transaction
    // that writes the new row, so two concurrent appenders racing on
    // BEGIN IMMEDIATE could both observe the same prev_hash and chain off it,
    // breaking the audit trail. SQLite serializes WRITES by file lock, but
    // we still need a transaction boundary that covers the read of the
    // previous hash AND the insert. _gate guarantees in-process serialization
    // for the common case; BEGIN IMMEDIATE guarantees cross-process safety
    // (e.g. a future reports tool reading via its own connection).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EventLogRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS event_log (
                  id               INTEGER PRIMARY KEY AUTOINCREMENT,
                  ts               TEXT    NOT NULL,
                  event_type       TEXT    NOT NULL,
                  station_id       TEXT,
                  acceptor_id      TEXT,
                  amount_pesos     INTEGER,
                  seconds_credited INTEGER,
                  payload          TEXT    NOT NULL,
                  prev_hash        TEXT    NOT NULL,
                  row_hash         TEXT    NOT NULL
                );
                """;
            await tableCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await CreateIndexIfMissingAsync(
            connection,
            "idx_event_log_ts",
            "CREATE INDEX IF NOT EXISTS idx_event_log_ts ON event_log(ts);",
            cancellationToken);
        await CreateIndexIfMissingAsync(
            connection,
            "idx_event_log_type_ts",
            "CREATE INDEX IF NOT EXISTS idx_event_log_type_ts ON event_log(event_type, ts);",
            cancellationToken);
        await CreateIndexIfMissingAsync(
            connection,
            "idx_event_log_station_ts",
            "CREATE INDEX IF NOT EXISTS idx_event_log_station_ts ON event_log(station_id, ts) WHERE station_id IS NOT NULL;",
            cancellationToken);

        await CreateTriggerIfMissingAsync(
            connection,
            "event_log_no_update",
            """
            CREATE TRIGGER IF NOT EXISTS event_log_no_update
            BEFORE UPDATE ON event_log BEGIN
              SELECT RAISE(ABORT, 'event_log is append-only');
            END;
            """,
            cancellationToken);
        await CreateTriggerIfMissingAsync(
            connection,
            "event_log_no_delete",
            """
            CREATE TRIGGER IF NOT EXISTS event_log_no_delete
            BEFORE DELETE ON event_log BEGIN
              SELECT RAISE(ABORT, 'event_log is append-only');
            END;
            """,
            cancellationToken);
    }

    public async Task<long> AppendAsync(
        EventLogAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            var prevHash = await ReadLastRowHashAsync(connection, transaction, cancellationToken);

            // Predict next id deterministically inside the write transaction so
            // we can include it in the hash before INSERT (the table's
            // event_log_no_update trigger forbids UPDATE-after-insert
            // patterns).
            var nextId = await ReadNextIdAsync(connection, transaction, cancellationToken);

            var rowHash = EventLogHasher.Compute(
                nextId,
                request.Ts,
                request.EventType,
                request.StationId,
                request.AcceptorId,
                request.AmountPesos,
                request.SecondsCredited,
                request.Payload,
                prevHash);

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO event_log (
                      id, ts, event_type, station_id, acceptor_id,
                      amount_pesos, seconds_credited, payload,
                      prev_hash, row_hash
                    ) VALUES (
                      $id, $ts, $event_type, $station_id, $acceptor_id,
                      $amount_pesos, $seconds_credited, $payload,
                      $prev_hash, $row_hash
                    );
                    """;
                insert.Parameters.AddWithValue("$id", nextId);
                insert.Parameters.AddWithValue("$ts", request.Ts);
                insert.Parameters.AddWithValue("$event_type", request.EventType);
                insert.Parameters.AddWithValue("$station_id", (object?)request.StationId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$acceptor_id", (object?)request.AcceptorId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$amount_pesos", (object?)request.AmountPesos ?? DBNull.Value);
                insert.Parameters.AddWithValue("$seconds_credited", (object?)request.SecondsCredited ?? DBNull.Value);
                insert.Parameters.AddWithValue("$payload", request.Payload);
                insert.Parameters.AddWithValue("$prev_hash", prevHash);
                insert.Parameters.AddWithValue("$row_hash", rowHash);

                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return nextId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EventLogRecord>> QueryAsync(
        EventLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        var sql = new System.Text.StringBuilder(
            """
            SELECT
              id, ts, event_type, station_id, acceptor_id,
              amount_pesos, seconds_credited, payload,
              prev_hash, row_hash
            FROM event_log
            WHERE 1=1
            """);

        if (!string.IsNullOrWhiteSpace(query.FromTs))
        {
            sql.AppendLine().Append("  AND ts >= $from_ts");
            command.Parameters.AddWithValue("$from_ts", query.FromTs);
        }

        if (!string.IsNullOrWhiteSpace(query.ToTs))
        {
            sql.AppendLine().Append("  AND ts < $to_ts");
            command.Parameters.AddWithValue("$to_ts", query.ToTs);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            sql.AppendLine().Append("  AND event_type = $event_type");
            command.Parameters.AddWithValue("$event_type", query.EventType);
        }

        if (!string.IsNullOrWhiteSpace(query.StationId))
        {
            sql.AppendLine().Append("  AND station_id = $station_id");
            command.Parameters.AddWithValue("$station_id", query.StationId);
        }

        sql.AppendLine().Append("ORDER BY id ASC");

        if (query.Limit is > 0)
        {
            sql.AppendLine().Append("LIMIT $limit");
            command.Parameters.AddWithValue("$limit", query.Limit.Value);

            if (query.Offset is > 0)
            {
                sql.Append(" OFFSET $offset");
                command.Parameters.AddWithValue("$offset", query.Offset.Value);
            }
        }

        sql.Append(';');
        command.CommandText = sql.ToString();

        var results = new List<EventLogRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    /// <summary>
    /// Walks the chain in id order, recomputes each row_hash, and confirms each
    /// row's prev_hash matches the previous row's row_hash. Returns the id of
    /// the first tampered row, or null when the chain is intact.
    /// </summary>
    public async Task<long?> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              id, ts, event_type, station_id, acceptor_id,
              amount_pesos, seconds_credited, payload,
              prev_hash, row_hash
            FROM event_log
            ORDER BY id ASC;
            """;

        var expectedPrev = EventLogHasher.GenesisPrevHash;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = ReadRecord(reader);

            if (!string.Equals(row.PrevHash, expectedPrev, StringComparison.Ordinal))
            {
                return row.Id;
            }

            var recomputed = EventLogHasher.Compute(
                row.Id,
                row.Ts,
                row.EventType,
                row.StationId,
                row.AcceptorId,
                row.AmountPesos,
                row.SecondsCredited,
                row.Payload,
                row.PrevHash);

            if (!string.Equals(recomputed, row.RowHash, StringComparison.Ordinal))
            {
                return row.Id;
            }

            expectedPrev = row.RowHash;
        }

        return null;
    }

    private static async Task<string> ReadLastRowHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT row_hash FROM event_log ORDER BY id DESC LIMIT 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string s ? s : EventLogHasher.GenesisPrevHash;
    }

    private static async Task<long> ReadNextIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM event_log;";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 1 : Convert.ToInt64(result);
    }

    private static async Task CreateIndexIfMissingAsync(
        SqliteConnection connection,
        string indexName,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateTriggerIfMissingAsync(
        SqliteConnection connection,
        string triggerName,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static EventLogRecord ReadRecord(SqliteDataReader reader)
    {
        return new EventLogRecord(
            Id: reader.GetInt64(0),
            Ts: reader.GetString(1),
            EventType: reader.GetString(2),
            StationId: reader.IsDBNull(3) ? null : reader.GetString(3),
            AcceptorId: reader.IsDBNull(4) ? null : reader.GetString(4),
            AmountPesos: reader.IsDBNull(5) ? null : reader.GetInt32(5),
            SecondsCredited: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Payload: reader.GetString(7),
            PrevHash: reader.GetString(8),
            RowHash: reader.GetString(9));
    }
}
