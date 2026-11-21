namespace RentalServer.Core.EventLog;

/// <summary>
/// One row of the append-only, hash-chained audit log. NULL-able fields preserve
/// the SQL schema's wide-table shape so the same record type fits every
/// event type (coin credits carry station + acceptor + pesos, rate publishes
/// carry none of those, etc.).
/// </summary>
public sealed record EventLogRecord(
    long Id,
    string Ts,
    string EventType,
    string? StationId,
    string? AcceptorId,
    int? AmountPesos,
    int? SecondsCredited,
    string Payload,
    string PrevHash,
    string RowHash);
