namespace RentalServer.Core.EventLog;

/// <summary>
/// The mutable shape callers fill in. The repository assigns id, prev_hash, and
/// row_hash itself inside the append transaction, so this record carries only
/// the fields the caller owns. Ts is supplied by the caller (not "now") so
/// the test suite can wind time forward deterministically and so an event
/// emitted as part of a wider transaction can share a single wall-clock
/// stamp.
/// </summary>
public sealed record EventLogAppendRequest(
    string Ts,
    string EventType,
    string? StationId,
    string? AcceptorId,
    int? AmountPesos,
    int? SecondsCredited,
    string Payload);
