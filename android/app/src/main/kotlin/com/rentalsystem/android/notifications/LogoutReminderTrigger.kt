package com.rentalsystem.android.notifications

import com.rentalsystem.android.sessions.SessionTrackerState

/**
 * Decides when to fire the one-shot "log out of your game accounts" reminder.
 *
 * Mirrors CountdownWarningService: fires once when remaining time first drops
 * to <= thresholdSec, re-arms if time is added back above the threshold or the
 * session id changes, and stays silent while locked / no-session / expired.
 * Pure logic, no Android deps, so it is JVM-unit-testable.
 *
 * Gated on its own `enabled` flag (NOT countdownWarning.enabled) so muting the
 * warning beeps does not also remove the privacy reminder.
 */
class LogoutReminderTrigger(
    private val enabled: Boolean,
    private val thresholdSec: Int
) {
    private var sessionId: String? = null
    private var shown = false

    /** Returns true exactly once per arming when the reminder should fire now. */
    fun update(state: SessionTrackerState): Boolean {
        if (!enabled || !state.hasSession || state.locked || state.effectiveRemainingSec <= 0) {
            reset()
            return false
        }

        if (sessionId != state.sessionId) {
            reset()
            sessionId = state.sessionId
        }

        if (state.effectiveRemainingSec > thresholdSec) {
            shown = false
            return false
        }

        if (!shown) {
            shown = true
            return true
        }
        return false
    }

    private fun reset() {
        sessionId = null
        shown = false
    }
}
