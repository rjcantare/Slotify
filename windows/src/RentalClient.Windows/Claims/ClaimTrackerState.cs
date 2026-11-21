namespace RentalClient.Windows.Claims;

public enum ClaimStatus
{
    Idle,
    Pending,
    Granted,
    Denied,
    TimedOut
}

// Tracks credits added during the current Granted multi-coin window.
public sealed record ClaimTrackerState(
    ClaimStatus Status,
    string? RequestId,
    string? ClaimId,
    string? AcceptorId,
    long WindowSec,
    long ElapsedSec,
    string? DenyReason,
    string? HeldBy,
    long RunningTotalSec,
    int RunningTotalPesos,
    string? BanUntil)
{
    public static ClaimTrackerState Idle { get; } =
        new(ClaimStatus.Idle, null, null, null, 0, 0, null, null, 0, 0, null);

    public long GrantedRemainingSec => Math.Max(0, WindowSec - ElapsedSec);

    // DONE button is hidden for the first 5 s of a Granted window so the user
    // doesn't accidentally end before adding any coins. Per CLAUDE.md S18.0 spec.
    public const long DoneButtonLockoutSec = 0;
    public bool CanShowDoneButton =>
        Status == ClaimStatus.Granted && ElapsedSec >= DoneButtonLockoutSec;
}
