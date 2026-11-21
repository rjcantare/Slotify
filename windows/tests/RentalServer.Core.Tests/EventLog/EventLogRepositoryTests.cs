using System.Globalization;
using Microsoft.Data.Sqlite;
using RentalServer.Core.EventLog;

namespace RentalServer.Core.Tests.EventLog;

public sealed class EventLogRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private EventLogRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-eventlog-{Guid.NewGuid():N}.db");
        _repository = new EventLogRepository(_databasePath);
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Empty_log_verifies_clean()
    {
        Assert.Null(await _repository.VerifyChainAsync());

        var rows = await _repository.QueryAsync(new EventLogQuery());
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Append_assigns_monotonic_ids_and_chains_hashes()
    {
        var id1 = await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));
        var id2 = await _repository.AppendAsync(MakeRequest("admin_credit", "pc-01", null, null, 600));
        var id3 = await _repository.AppendAsync(MakeRequest("rate_published", null, null, null, null));

        Assert.Equal(1, id1);
        Assert.Equal(2, id2);
        Assert.Equal(3, id3);

        var rows = await _repository.QueryAsync(new EventLogQuery());
        Assert.Equal(3, rows.Count);

        // First row chains off genesis.
        Assert.Equal(EventLogHasher.GenesisPrevHash, rows[0].PrevHash);
        Assert.Equal(rows[0].RowHash, rows[1].PrevHash);
        Assert.Equal(rows[1].RowHash, rows[2].PrevHash);

        Assert.Null(await _repository.VerifyChainAsync());
    }

    [Fact]
    public async Task Filter_by_event_type_returns_only_matching_rows()
    {
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));
        await _repository.AppendAsync(MakeRequest("admin_credit", "pc-01", null, null, 600));
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-02", "coin-01", 20, 1800));

        var coinOnly = await _repository.QueryAsync(new EventLogQuery(EventType: "coin_credit"));
        Assert.Equal(2, coinOnly.Count);
        Assert.All(coinOnly, r => Assert.Equal("coin_credit", r.EventType));
    }

    [Fact]
    public async Task Filter_by_station_id_and_time_window_combines_correctly()
    {
        var t1 = "2026-05-21T01:00:00.000Z";
        var t2 = "2026-05-21T01:05:00.000Z";
        var t3 = "2026-05-21T01:10:00.000Z";

        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360, ts: t1));
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-02", "coin-01", 5, 360, ts: t2));
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 20, 1800, ts: t3));

        var window = await _repository.QueryAsync(new EventLogQuery(
            FromTs: t1,
            ToTs: t3,                  // half-open: t3 excluded
            StationId: "pc-01"));

        Assert.Single(window);
        Assert.Equal(t1, window[0].Ts);
    }

    [Fact]
    public async Task Update_is_blocked_by_trigger()
    {
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE event_log SET payload = 'tampered' WHERE id = 1;";

        var ex = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_is_blocked_by_trigger()
    {
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM event_log WHERE id = 1;";

        var ex = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyChain_returns_id_of_tampered_row()
    {
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));
        await _repository.AppendAsync(MakeRequest("admin_credit", "pc-01", null, null, 600));
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-02", "coin-01", 20, 1800));

        // Drop the no_update trigger, mutate id=2 payload, restore the trigger.
        // This simulates an attacker bypassing the application but leaving SQLite
        // intact — exactly the case the hash chain catches.
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();

            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER IF EXISTS event_log_no_update;";
                await drop.ExecuteNonQueryAsync();
            }

            await using (var tamper = connection.CreateCommand())
            {
                tamper.CommandText = "UPDATE event_log SET payload = 'tampered' WHERE id = 2;";
                await tamper.ExecuteNonQueryAsync();
            }
        }

        var firstBadId = await _repository.VerifyChainAsync();
        Assert.Equal(2, firstBadId);
    }

    [Fact]
    public async Task VerifyChain_catches_prev_hash_mismatch_after_row_deleted()
    {
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));
        await _repository.AppendAsync(MakeRequest("admin_credit", "pc-01", null, null, 600));
        await _repository.AppendAsync(MakeRequest("coin_credit", "pc-02", "coin-01", 20, 1800));

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();

            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER IF EXISTS event_log_no_delete;";
                await drop.ExecuteNonQueryAsync();
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM event_log WHERE id = 2;";
                await delete.ExecuteNonQueryAsync();
            }
        }

        // After deletion the chain is row 1 → row 3, but row 3's prev_hash
        // still points at row 2's hash. Verifier catches that on row 3.
        var firstBadId = await _repository.VerifyChainAsync();
        Assert.Equal(3, firstBadId);
    }

    [Fact]
    public async Task Limit_and_offset_paginate_in_id_order()
    {
        for (var i = 0; i < 10; i++)
        {
            await _repository.AppendAsync(MakeRequest("coin_credit", "pc-01", "coin-01", 5, 360));
        }

        var page1 = await _repository.QueryAsync(new EventLogQuery(Limit: 3));
        var page2 = await _repository.QueryAsync(new EventLogQuery(Limit: 3, Offset: 3));
        var page3 = await _repository.QueryAsync(new EventLogQuery(Limit: 3, Offset: 6));

        Assert.Equal(new long[] { 1, 2, 3 }, page1.Select(r => r.Id));
        Assert.Equal(new long[] { 4, 5, 6 }, page2.Select(r => r.Id));
        Assert.Equal(new long[] { 7, 8, 9 }, page3.Select(r => r.Id));
    }

    [Fact]
    public async Task Null_columns_round_trip_through_hash_and_storage()
    {
        // rate_published rows carry no station / acceptor / pesos / seconds —
        // those nullables must survive the round trip and not affect the chain.
        var id = await _repository.AppendAsync(MakeRequest("rate_published", null, null, null, null, payload: "{}"));
        var rows = await _repository.QueryAsync(new EventLogQuery());

        Assert.Single(rows);
        Assert.Equal(id, rows[0].Id);
        Assert.Null(rows[0].StationId);
        Assert.Null(rows[0].AcceptorId);
        Assert.Null(rows[0].AmountPesos);
        Assert.Null(rows[0].SecondsCredited);

        Assert.Null(await _repository.VerifyChainAsync());
    }

    private static EventLogAppendRequest MakeRequest(
        string eventType,
        string? stationId,
        string? acceptorId,
        int? amountPesos,
        int? secondsCredited,
        string? ts = null,
        string payload = "{}")
    {
        return new EventLogAppendRequest(
            Ts: ts ?? FormatUtcNow(),
            EventType: eventType,
            StationId: stationId,
            AcceptorId: acceptorId,
            AmountPesos: amountPesos,
            SecondsCredited: secondsCredited,
            Payload: payload);
    }

    private static string FormatUtcNow()
    {
        return DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
