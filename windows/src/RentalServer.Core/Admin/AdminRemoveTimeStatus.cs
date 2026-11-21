namespace RentalServer.Core.Admin;

public enum AdminRemoveTimeStatus
{
    Applied,
    StationUnknown,
    StationOffline,
    /// <summary>
    /// Station has no active session, so there's no time to remove. Distinct
    /// from StationUnknown — the station IS registered, just not in a session.
    /// </summary>
    NoActiveSession,
    /// <summary>
    /// Session exists but is already at zero (locked, or freshly zeroed). The
    /// remove was a no-op; the UI should say "already at 0" rather than show
    /// a misleading "removed 30 min" message.
    /// </summary>
    NothingToRemove
}
