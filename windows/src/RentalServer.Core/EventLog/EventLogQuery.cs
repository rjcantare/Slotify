namespace RentalServer.Core.EventLog;

/// <summary>
/// Filter shape for QueryAsync. All filters are AND'd; null/whitespace fields
/// are ignored. Ts bounds are half-open [from, to) on ISO 8601 string
/// comparison — safe because ISO 8601 strings are lexicographically
/// orderable in UTC zulu form.
/// </summary>
public sealed record EventLogQuery(
    string? FromTs = null,
    string? ToTs = null,
    string? EventType = null,
    string? StationId = null,
    int? Limit = null,
    int? Offset = null);
