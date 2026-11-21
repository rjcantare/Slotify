namespace RentalServer.Core.Sessions;

/// <summary>
/// Result of moving live remaining time from one station session to another.
/// The transfer does not mint credit events; the retained session payloads are
/// the source of truth for both clients after the move.
/// </summary>
public sealed record TransferTimeResult(
    SessionState SourceSession,
    SessionState TargetSession,
    int SecondsTransferred);
