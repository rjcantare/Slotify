namespace RentalServer.Core.Sessions;

/// <summary>
/// Outcome of <see cref="SessionService.RemoveTimeAsync"/>. Returned to the
/// caller (AdminTimeService → WPF host) so the status text can distinguish
/// "fully removed N seconds" from "session already at 0, nothing to remove."
/// </summary>
/// <param name="Session">The session state after the remove operation.</param>
/// <param name="ActualRemovedSec">
/// Seconds actually subtracted. May be smaller than requested when the
/// session had less time left than the requested amount.
/// </param>
/// <param name="NowLocked">
/// True when the remove brought the session to zero and it re-locked as a
/// result. Useful for status messaging on the operator UI.
/// </param>
public sealed record RemoveTimeResult(
    SessionState Session,
    int ActualRemovedSec,
    bool NowLocked);
