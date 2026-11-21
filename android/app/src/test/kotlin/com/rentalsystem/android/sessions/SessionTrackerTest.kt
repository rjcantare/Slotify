package com.rentalsystem.android.sessions

import com.rentalsystem.android.mqtt.SessionMessage
import org.junit.Assert.assertEquals
import org.junit.Test

class SessionTrackerTest {
    private var elapsedMs = 0L
    private var wallMs = 1_000_000L

    private val tracker = SessionTracker(
        elapsedRealtimeMs = { elapsedMs },
        currentTimeMillis = { wallMs }
    )

    @Test
    fun appliesRetainedSessionAndTicksMonotonically() {
        tracker.apply(message(remainingSec = 120))

        elapsedMs += 5_000

        assertEquals(115, tracker.getCurrentState().effectiveRemainingSec)
    }

    @Test
    fun duplicateRetainedSessionDoesNotReanchorCountdown() {
        val message = message(remainingSec = 120)
        var emits = 0
        tracker.onStateChange = { emits++ }

        tracker.apply(message)
        elapsedMs += 5_000
        tracker.apply(message)

        assertEquals(1, emits)
        assertEquals(115, tracker.getCurrentState().effectiveRemainingSec)
    }

    @Test
    fun olderCreditSeqNoIsIgnoredWithinSameSession() {
        tracker.apply(message(sessionId = "s-1", remainingSec = 60, lastCreditSeqNo = 2))
        tracker.apply(message(sessionId = "s-1", remainingSec = 999, lastCreditSeqNo = 1))

        // Same sessionId + lower seqno = stale retained on reconnect, rejected.
        assertEquals(60, tracker.getCurrentState().effectiveRemainingSec)
    }

    @Test
    fun lowerSeqNoIsAcceptedAcrossSessionIdChange() {
        // Server restarts and re-mints the session (new sessionId). lastCreditSeqNo
        // legitimately resets to a low number on the freshly-keyed session. Without
        // accepting this, the tracker stays stuck on the pre-reset view and the
        // S28f credit-dedup realignment hook never fires.
        tracker.apply(message(sessionId = "s-1", remainingSec = 60, lastCreditSeqNo = 80))
        tracker.apply(message(sessionId = "s-2", remainingSec = 999, lastCreditSeqNo = 1))

        val state = tracker.getCurrentState()
        assertEquals("s-2", state.sessionId)
        assertEquals(1, state.lastCreditSeqNo)
        assertEquals(999, state.effectiveRemainingSec)
    }

    @Test
    fun equalSeqNoStructuralChangeCanLockSession() {
        tracker.apply(message(remainingSec = 60, locked = false, lastCreditSeqNo = 2))
        tracker.apply(message(remainingSec = 0, locked = true, lastCreditSeqNo = 2))

        val state = tracker.getCurrentState()
        assertEquals(true, state.locked)
        assertEquals(0, state.effectiveRemainingSec)
    }

    @Test
    fun wallClockCatchupAppliesOnlyOnce() {
        val updatedAt = "1970-01-01T00:16:30Z"
        wallMs = 1_000_000L

        tracker.apply(message(remainingSec = 120, updatedAt = updatedAt))
        elapsedMs += 10_000

        assertEquals(100, tracker.getCurrentState().effectiveRemainingSec)
    }

    private fun message(
        remainingSec: Int,
        locked: Boolean = false,
        lastCreditSeqNo: Long = 1,
        updatedAt: String = "1970-01-01T00:16:40Z",
        sessionId: String = "s-1"
    ): SessionMessage =
        SessionMessage(
            sessionId = sessionId,
            stationId = "tab-01",
            deviceType = "tab",
            locked = locked,
            remainingSec = remainingSec,
            lastCreditSeqNo = lastCreditSeqNo,
            updatedAt = updatedAt
        )
}
