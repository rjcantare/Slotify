using Microsoft.Data.Sqlite;
using RentalServer.Core.Persistence;

namespace RentalServer.Core.Claims;

public sealed class ClaimRepository
{
    private const int SqliteExtendedConstraintUnique = 2067;

    private readonly string _databasePath;

    public ClaimRepository(string databasePath)
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
                CREATE TABLE IF NOT EXISTS claims (
                  claim_id            TEXT PRIMARY KEY,
                  request_id          TEXT NOT NULL,
                  station_id          TEXT NOT NULL REFERENCES stations(station_id),
                  acceptor_id         TEXT NOT NULL REFERENCES acceptors(acceptor_id),
                  granted_at          TEXT NOT NULL,
                  expires_at          TEXT NOT NULL,
                  resolved_at         TEXT,
                  status              TEXT NOT NULL CHECK (status IN ('pending', 'fulfilled', 'expired', 'cancelled')),
                  rate_mode           TEXT NOT NULL DEFAULT 'insert_coin' CHECK (rate_mode IN ('insert_coin', 'add_time')),
                  accumulated_pesos   INTEGER NOT NULL DEFAULT 0,
                  accumulated_credits INTEGER NOT NULL DEFAULT 0
                );
                """;
            await tableCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddColumnIfMissingAsync(
            connection,
            "ALTER TABLE claims ADD COLUMN accumulated_credits INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await AddColumnIfMissingAsync(
            connection,
            "ALTER TABLE claims ADD COLUMN rate_mode TEXT NOT NULL DEFAULT 'insert_coin' CHECK (rate_mode IN ('insert_coin', 'add_time'));",
            cancellationToken);
        await AddColumnIfMissingAsync(
            connection,
            "ALTER TABLE claims ADD COLUMN accumulated_pesos INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);

        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText =
                """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_claims_pending_per_acceptor
                  ON claims(acceptor_id) WHERE status = 'pending';
                """;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<bool> TryAddAccumulatedCreditsAsync(
        string claimId,
        int secondsToAdd,
        CancellationToken cancellationToken = default)
    {
        if (secondsToAdd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secondsToAdd));
        }

        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE claims
            SET accumulated_credits = accumulated_credits + $delta
            WHERE claim_id = $claim_id AND status = 'pending';
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);
        command.Parameters.AddWithValue("$delta", secondsToAdd);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<int> GetAccumulatedCreditsAsync(
        string claimId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT accumulated_credits FROM claims WHERE claim_id = $claim_id;
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<bool> TryAddAccumulatedPesosAsync(
        string claimId,
        int pesos,
        CancellationToken cancellationToken = default)
    {
        if (pesos <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pesos));
        }

        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE claims
            SET accumulated_pesos = accumulated_pesos + $delta
            WHERE claim_id = $claim_id AND status = 'pending';
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);
        command.Parameters.AddWithValue("$delta", pesos);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task ExtendExpiryAsync(
        string claimId,
        string newExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE claims
            SET expires_at = $expires_at
            WHERE claim_id = $claim_id AND status = 'pending';
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);
        command.Parameters.AddWithValue("$expires_at", newExpiresAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetAccumulatedPesosAsync(
        string claimId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT accumulated_pesos FROM claims WHERE claim_id = $claim_id;
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<bool> TryInsertPendingAsync(
        ClaimRecord claim,
        CancellationToken cancellationToken = default)
    {
        if (claim.Status != ClaimStatus.Pending)
        {
            throw new ArgumentException("Initial claim insert must be Pending.", nameof(claim));
        }

        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO claims (
              claim_id,
              request_id,
              station_id,
              acceptor_id,
              granted_at,
              expires_at,
              resolved_at,
              status,
              rate_mode
            )
            VALUES (
              $claim_id,
              $request_id,
              $station_id,
              $acceptor_id,
              $granted_at,
              $expires_at,
              NULL,
              'pending',
              $rate_mode
            );
            """;

        command.Parameters.AddWithValue("$claim_id", claim.ClaimId);
        command.Parameters.AddWithValue("$request_id", claim.RequestId);
        command.Parameters.AddWithValue("$station_id", claim.StationId);
        command.Parameters.AddWithValue("$acceptor_id", claim.AcceptorId);
        command.Parameters.AddWithValue("$granted_at", claim.GrantedAt);
        command.Parameters.AddWithValue("$expires_at", claim.ExpiresAt);
        command.Parameters.AddWithValue("$rate_mode", claim.RateMode.ToDbValue());

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteExtendedConstraintUnique)
        {
            return false;
        }
    }

    public async Task<ClaimRecord?> GetPendingForAcceptorAsync(
        string acceptorId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              claim_id, request_id, station_id, acceptor_id,
              granted_at, expires_at, resolved_at, status, rate_mode
            FROM claims
            WHERE acceptor_id = $acceptor_id AND status = 'pending'
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$acceptor_id", acceptorId);

        return await ReadOptionalAsync(command, cancellationToken);
    }

    public async Task<ClaimRecord?> GetByIdAsync(
        string claimId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
              claim_id, request_id, station_id, acceptor_id,
              granted_at, expires_at, resolved_at, status, rate_mode
            FROM claims
            WHERE claim_id = $claim_id;
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);

        return await ReadOptionalAsync(command, cancellationToken);
    }

    public async Task<bool> TryResolvePendingAsync(
        string claimId,
        ClaimStatus terminalStatus,
        string resolvedAt,
        CancellationToken cancellationToken = default)
    {
        if (terminalStatus == ClaimStatus.Pending)
        {
            throw new ArgumentException("Resolution status cannot be Pending.", nameof(terminalStatus));
        }

        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE claims
            SET status = $status, resolved_at = $resolved_at
            WHERE claim_id = $claim_id AND status = 'pending';
            """;

        command.Parameters.AddWithValue("$claim_id", claimId);
        command.Parameters.AddWithValue("$status", terminalStatus.ToDbValue());
        command.Parameters.AddWithValue("$resolved_at", resolvedAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<int> ExpireAllPendingAsync(
        string resolvedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await RentalDatabase.OpenConnectionAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE claims
            SET status = 'expired', resolved_at = $resolved_at
            WHERE status = 'pending';
            """;

        command.Parameters.AddWithValue("$resolved_at", resolvedAt);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ClaimRecord?> ReadOptionalAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimRecord(
            ClaimId: reader.GetString(0),
            RequestId: reader.GetString(1),
            StationId: reader.GetString(2),
            AcceptorId: reader.GetString(3),
            GrantedAt: reader.GetString(4),
            ExpiresAt: reader.GetString(5),
            ResolvedAt: reader.IsDBNull(6) ? null : reader.GetString(6),
            Status: ClaimStatusExtensions.FromDbValue(reader.GetString(7)),
            RateMode: ClaimRateModeExtensions.FromDbValue(reader.GetString(8)));
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
}
