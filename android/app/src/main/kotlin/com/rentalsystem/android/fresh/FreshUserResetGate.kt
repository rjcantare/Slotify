package com.rentalsystem.android.fresh

import com.rentalsystem.android.sessions.SessionTrackerState

/**
 * One-shot transition guard for the tablet fresh-user cleanup.
 *
 * Cleanup fires once per active→locked cycle. When the server re-uses the
 * same sessionId across multiple Insert Coin / Clear Time cycles (typical
 * for a single customer's continuous rental), the gate resets when the
 * session goes active again, allowing cleanup on each subsequent lock.
 *
 * Startup locked state (retained session already locked) is skipped: we
 * require observing this sessionId as active before allowing cleanup.
 */
class FreshUserResetGate {
    private var activeSessionId: String? = null
    private val completedSessionIds = mutableSetOf<String>()

    fun cleanupSessionIdIfEnded(state: SessionTrackerState): String? {
        if (!state.hasSession || state.sessionId.isBlank()) {
            return null
        }

        val active = !state.locked && state.effectiveRemainingSec > 0
        if (active) {
            activeSessionId = state.sessionId
            // Reset the gate so the next lock fires cleanup again.
            // The server re-uses sessionId across Insert Coin cycles;
            // we want one cleanup per active→locked cycle.
            completedSessionIds.remove(state.sessionId)
            return null
        }

        if (activeSessionId != state.sessionId) {
            return null
        }

        return if (completedSessionIds.add(state.sessionId)) {
            state.sessionId
        } else {
            null
        }
    }
}
