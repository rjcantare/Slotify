package com.rentalsystem.android.fresh

import com.rentalsystem.android.sessions.SessionTrackerState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FreshUserResetGateTest {
    private val gate = FreshUserResetGate()

    @Test
    fun lockedRetainedSessionOnStartupDoesNotTriggerCleanup() {
        assertNull(gate.cleanupSessionIdIfEnded(state(sessionId = "s-1", active = false)))
    }

    @Test
    fun activeThenLockedTriggersCleanupOnce() {
        assertNull(gate.cleanupSessionIdIfEnded(state(sessionId = "s-1", active = true)))
        assertEquals("s-1", gate.cleanupSessionIdIfEnded(state(sessionId = "s-1", active = false)))
        assertNull(gate.cleanupSessionIdIfEnded(state(sessionId = "s-1", active = false)))
    }

    @Test
    fun secondSessionCanTriggerItsOwnCleanup() {
        assertNull(gate.cleanupSessionIdIfEnded(state(sessionId = "s-1", active = true)))
        assertEquals("s-1", gate.cleanupSessionIdIfEnded(state(sessionId = "s-1", active = false)))

        assertNull(gate.cleanupSessionIdIfEnded(state(sessionId = "s-2", active = true)))
        assertEquals("s-2", gate.cleanupSessionIdIfEnded(state(sessionId = "s-2", active = false)))
    }

    private fun state(sessionId: String, active: Boolean): SessionTrackerState =
        SessionTrackerState(
            hasSession = true,
            sessionId = sessionId,
            stationId = "tab-01",
            deviceType = "tab",
            locked = !active,
            effectiveRemainingSec = if (active) 60 else 0,
            lastCreditSeqNo = 1,
            serverUpdatedAt = "2026-05-26T00:00:00Z"
        )
}
