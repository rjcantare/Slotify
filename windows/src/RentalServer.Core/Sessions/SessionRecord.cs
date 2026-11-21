namespace RentalServer.Core.Sessions;

// DB-shaped row from the `sessions` table. The in-memory "anchored" form used by
// SessionService for live countdown math is built on top of this in a later step
// (SessionState carries the monotonic anchor; that value is runtime-only and is
// not persisted).
public sealed record SessionRecord(
    string SessionId,
    string StationId,
    string StartedAt,
    string? EndedAt,
    int RemainingSeconds,
    bool IsLocked,
    long LastCreditSeqNo,
    string UpdatedAt);
