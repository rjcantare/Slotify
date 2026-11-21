namespace RentalServer.Core.EventLog;

/// <summary>
/// Canonical event_type strings. Used by every service that writes to the
/// audit log so reports can filter without hardcoding strings on the read
/// side. Past values must never change once they're in production —
/// existing rows in event_log reference them.
/// </summary>
public static class EventTypes
{
    public const string CoinCredit = "coin_credit";
    public const string AdminCredit = "admin_credit";

    /// <summary>
    /// Emitted when the operator removes time from an active session via the
    /// server's Remove Time buttons (inverse of admin Add Time). Payload
    /// carries the previous live remaining + the actual seconds removed
    /// (which can be less than the requested amount if the session had less
    /// time than that left). Tracked separately from admin_credit so the
    /// Coin History view can show "Add Time (admin)" vs "Remove Time (admin)"
    /// without overloading the sign of seconds_credited.
    /// </summary>
    public const string AdminRemoveTime = "admin_remove_time";
    public const string AdminTransferTime = "admin_transfer_time";
    public const string ClaimGranted = "claim_granted";
    public const string ClaimEnded = "claim_ended";
    public const string ClaimExpired = "claim_expired";
    public const string ClaimCancelled = "claim_cancelled";
    public const string SessionStarted = "session_started";
    public const string SessionLocked = "session_locked";
    public const string SessionCleared = "session_cleared";
    public const string RatePublished = "rate_published";
    public const string TabletAppsPublished = "tablet_apps_published";
    public const string TabletAdminPasswordPublished = "tablet_admin_password_published";
    public const string PcPolicyPublished = "pc_policy_published";

    /// <summary>
    /// Emitted when the server detects an ESP32 NVS-seqno reset — a large
    /// backward jump in the incoming pulse seqno relative to the stored
    /// dedup pointer (see PulseService.NvsResetBackwardJumpThreshold) — and
    /// realigns last_processed_pulse_seqno down to just below the incoming
    /// seqno so subsequent pulses get credited. Defensive — without this
    /// realignment, every pulse after the reset would be silently dropped as
    /// stale (coins accepted, no credit). See failure-mode-audit-s28.md item E5.
    /// </summary>
    public const string AcceptorSeqNoReset = "acceptor_seqno_reset";

    // S49: server-issued admin commands. Each row records the publish
    // (always), not the ack — the OS-level reboot/shutdown variants race
    // the client process teardown so an ack is best-effort. The audit
    // intent is "operator initiated X on station Y", which is captured
    // at publish time regardless of whether the station survives long
    // enough to respond.
    public const string AdminRestartClient = "admin_restart_client";
    public const string AdminRebootPc = "admin_reboot_pc";
    public const string AdminShutdownPc = "admin_shutdown_pc";
}
