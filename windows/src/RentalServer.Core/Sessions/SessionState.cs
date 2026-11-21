namespace RentalServer.Core.Sessions;

// In-memory anchored shape of a session. The pair (RemainingSecondsAtAnchor,
// AnchorTimestamp) is the heart of the NTP-jump-immune countdown:
//   live remaining = max(0, RemainingSecondsAtAnchor - (clock.Now - AnchorTimestamp) / clock.Frequency)
// Both fields are re-stamped only on state transitions (start, credit, lock). AnchorTimestamp
// is a monotonic Stopwatch tick — never a wall-clock value — so NTP jumps cannot affect math.
public sealed record SessionState(
    string SessionId,
    string StationId,
    string DeviceType,
    string StartedAt,
    int RemainingSecondsAtAnchor,
    long AnchorTimestamp,
    bool IsLocked,
    long LastCreditSeqNo,
    string UpdatedAt);
