using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Credits;

// Per-credit retry queue. Schema matches the table defined in the MQTT
// Protocol section of CLAUDE.md (the schema file authoritative copy). A row
// is inserted by SessionService right after each successful credit publish
// and deleted by the server's credit/ack handler. A background sweep
// re-publishes any row whose age exceeds the 5 s ACK timeout.
//
// Survives server restarts because it's persisted; on cold start the sweep
// will republish anything that was outstanding when the previous process
// died. Stations dedup by `lastProcessedCreditSeqNo`, so a duplicate
// republish is safe — the station still ACKs (per protocol §7), which
// finally clears the row.
public sealed class PendingCreditsRepository
{
    private readonly string _databasePath;

    public PendingCreditsRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        // CLAUDE.md schema: composite key (station_id, seqno) so a station
        // can have multiple outstanding credits queued in monotonic order.
        // station_id FK ensures we never queue against an unregistered station.
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS pending_credits (
              seqno         INTEGER NOT NULL,
              station_id    TEXT    NOT NULL REFERENCES stations(station_id),
              payload       TEXT    NOT NULL,
              issued_at     TEXT    NOT NULL,
              last_retry_at TEXT,
              PRIMARY KEY (station_id, seqno)
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records a credit awaiting ACK. Idempotent on (station_id, seqno) —
    /// re-inserting the same row is a no-op so callers don't need to
    /// guard against double-write races between publish and tracker.
    /// </summary>
    public async Task InsertAsync(
        string stationId,
        long seqNo,
        string payload,
        string issuedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pending_credits (
              station_id,
              seqno,
              payload,
              issued_at,
              last_retry_at
            )
            VALUES (
              $station_id,
              $seqno,
              $payload,
              $issued_at,
              NULL
            )
            ON CONFLICT(station_id, seqno) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$seqno", seqNo);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$issued_at", issuedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the matching row when a credit/ack arrives. Returns the
    /// rows-affected count so the caller can log "ACK for unknown seqno"
    /// when stations re-ACK an already-cleared credit (which is allowed
    /// by protocol §7 — ACK even on duplicate).
    /// </summary>
    public async Task<int> DeleteAsync(
        string stationId,
        long seqNo,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM pending_credits
            WHERE station_id = $station_id AND seqno = $seqno;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$seqno", seqNo);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Rows whose effective last-attempt timestamp (issued_at OR last_retry_at
    /// when set) is older than <paramref name="staleBeforeUtc"/>. Returned in
    /// insertion order (PRIMARY KEY ascending) so the sweep republishes
    /// credits in the same monotonic order the station originally saw them.
    /// </summary>
    public async Task<IReadOnlyList<PendingCreditRecord>> ListStaleBeforeAsync(
        string staleBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT station_id, seqno, payload, issued_at, last_retry_at
            FROM pending_credits
            WHERE COALESCE(last_retry_at, issued_at) < $stale_before
            ORDER BY station_id, seqno;
            """;
        command.Parameters.AddWithValue("$stale_before", staleBeforeUtc);

        var results = new List<PendingCreditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PendingCreditRecord(
                StationId: reader.GetString(0),
                SeqNo: reader.GetInt64(1),
                Payload: reader.GetString(2),
                IssuedAt: reader.GetString(3),
                LastRetryAt: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return results;
    }

    public async Task MarkRetriedAsync(
        string stationId,
        long seqNo,
        string retryAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE pending_credits
            SET last_retry_at = $last_retry_at
            WHERE station_id = $station_id AND seqno = $seqno;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$seqno", seqNo);
        command.Parameters.AddWithValue("$last_retry_at", retryAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pending_credits;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }
}

public sealed record PendingCreditRecord(
    string StationId,
    long SeqNo,
    string Payload,
    string IssuedAt,
    string? LastRetryAt);
