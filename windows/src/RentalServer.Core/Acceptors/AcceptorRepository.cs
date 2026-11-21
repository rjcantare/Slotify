using System.Globalization;
using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Acceptors;

public sealed class AcceptorRepository
{
    private readonly string _databasePath;

    public AcceptorRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS acceptors (
              acceptor_id                TEXT    PRIMARY KEY,
              created_at                 TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
              last_seen_at               TEXT,
              firmware_version           TEXT,
              last_rssi                  INTEGER,
              is_online                  INTEGER NOT NULL DEFAULT 0,
              last_processed_pulse_seqno INTEGER NOT NULL DEFAULT 0,
              last_uptime_sec            INTEGER
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        // E5 migration: add last_uptime_sec to pre-existing tables. The
        // column is the boot-signal anchor for PulseService's NVS-reset
        // realignment path. NULL when the acceptor has never been seen
        // online since this column shipped.
        await AddColumnIfMissingAsync(
            connection,
            "ALTER TABLE acceptors ADD COLUMN last_uptime_sec INTEGER;",
            cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    public async Task UpsertOnlineAsync(
        string acceptorId,
        string? firmwareVersion,
        int? lastRssi,
        string lastSeenAt,
        long? uptimeSec = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        // First-seen acceptors get inserted with seqno = 0 (no pulses processed yet).
        // Re-seen acceptors keep their last_processed_pulse_seqno untouched — that
        // pointer is the dedup floor and must NOT be reset by a status update.
        //
        // E5: last_uptime_sec IS overwritten on every status because it's the
        // boot-signal anchor — a small uptimeSec on a re-seen acceptor means
        // the ESP32 just rebooted, which is exactly the signal PulseService
        // needs to know about during NVS-reset realignment.
        command.CommandText =
            """
            INSERT INTO acceptors (
              acceptor_id,
              firmware_version,
              last_rssi,
              last_seen_at,
              is_online,
              last_uptime_sec
            )
            VALUES (
              $acceptor_id,
              $firmware_version,
              $last_rssi,
              $last_seen_at,
              1,
              $last_uptime_sec
            )
            ON CONFLICT(acceptor_id) DO UPDATE SET
              firmware_version = excluded.firmware_version,
              last_rssi = excluded.last_rssi,
              last_seen_at = excluded.last_seen_at,
              is_online = excluded.is_online,
              last_uptime_sec = excluded.last_uptime_sec;
            """;

        command.Parameters.AddWithValue("$acceptor_id", acceptorId);
        command.Parameters.AddWithValue("$firmware_version", (object?)firmwareVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_rssi", (object?)lastRssi ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_seen_at", lastSeenAt);
        command.Parameters.AddWithValue("$last_uptime_sec", (object?)uptimeSec ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOfflineAsync(
        string acceptorId,
        string lastSeenAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE acceptors
            SET
              last_seen_at = $last_seen_at,
              is_online = 0
            WHERE acceptor_id = $acceptor_id;
            """;

        command.Parameters.AddWithValue("$acceptor_id", acceptorId);
        command.Parameters.AddWithValue("$last_seen_at", lastSeenAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AcceptorRecord?> GetAsync(
        string acceptorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              acceptor_id,
              created_at,
              last_seen_at,
              firmware_version,
              last_rssi,
              is_online,
              last_processed_pulse_seqno,
              last_uptime_sec
            FROM acceptors
            WHERE acceptor_id = $acceptor_id;
            """;

        command.Parameters.AddWithValue("$acceptor_id", acceptorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task<AcceptorRecord?> GetFirstOnlineAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              acceptor_id, created_at, last_seen_at, firmware_version,
              last_rssi, is_online, last_processed_pulse_seqno, last_uptime_sec
            FROM acceptors
            WHERE is_online = 1
            ORDER BY acceptor_id
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task<IReadOnlyList<AcceptorRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              acceptor_id,
              created_at,
              last_seen_at,
              firmware_version,
              last_rssi,
              is_online,
              last_processed_pulse_seqno,
              last_uptime_sec
            FROM acceptors
            ORDER BY acceptor_id;
            """;

        var acceptors = new List<AcceptorRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            acceptors.Add(ReadRecord(reader));
        }

        return acceptors;
    }

    private static AcceptorRecord ReadRecord(SqliteDataReader reader)
    {
        return new AcceptorRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetInt32(5) == 1,
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7));
    }

    // Updates last_processed_pulse_seqno only if `seqNo` is strictly greater than the
    // current value. Returns true when the row was actually advanced — i.e. this pulse
    // was new. Returns false when the seqno is a duplicate or stale (still publish ACK).
    public async Task<bool> TryAdvancePulseSeqNoAsync(
        string acceptorId,
        long seqNo,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE acceptors
            SET last_processed_pulse_seqno = $seq_no
            WHERE acceptor_id = $acceptor_id
              AND last_processed_pulse_seqno < $seq_no;
            """;

        command.Parameters.AddWithValue("$acceptor_id", acceptorId);
        command.Parameters.AddWithValue("$seq_no", seqNo);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <summary>
    /// E5: realigns last_processed_pulse_seqno DOWN to <paramref name="newPointer"/>.
    /// Only fires when newPointer is strictly less than the current value — never
    /// blindly resets a healthy counter. Caller (PulseService) gates this on the
    /// combined boot signal: incoming.seqNo == 1 AND last_uptime_sec is low.
    ///
    /// Returns the OLD last_processed value on a successful realign so the caller
    /// can write a meaningful audit row; returns null when no realignment happened
    /// (newPointer >= current, or acceptor row missing).
    /// </summary>
    public async Task<long?> RealignPulseSeqNoAsync(
        string acceptorId,
        long newPointer,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);

        // BEGIN IMMEDIATE so the read-then-update is atomic against another
        // pulse landing on the same acceptor (would be very rare given the
        // boot signal precondition, but defensive).
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT last_processed_pulse_seqno FROM acceptors WHERE acceptor_id = $acceptor_id;";
            read.Parameters.AddWithValue("$acceptor_id", acceptorId);

            var raw = await read.ExecuteScalarAsync(cancellationToken);
            if (raw is null || raw is DBNull)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            current = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }

        if (newPointer >= current)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE acceptors
                SET last_processed_pulse_seqno = $new_pointer
                WHERE acceptor_id = $acceptor_id;
                """;
            update.Parameters.AddWithValue("$acceptor_id", acceptorId);
            update.Parameters.AddWithValue("$new_pointer", newPointer);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return current;
    }
}
