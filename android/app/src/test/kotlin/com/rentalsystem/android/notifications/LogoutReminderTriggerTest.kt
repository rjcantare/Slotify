package com.rentalsystem.android.notifications

import com.rentalsystem.android.sessions.SessionTrackerState
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LogoutReminderTriggerTest {
    @Test
    fun firesOnceWhenCrossingThreshold() {
        val trigger = LogoutReminderTrigger(enabled = true, thresholdSec = 60)
        assertFalse(trigger.update(active(61)))
        assertTrue(trigger.update(active(60)))
        assertFalse(trigger.update(active(60)))
        assertFalse(trigger.update(active(59)))
    }

    @Test
    fun reArmsWhenTimeAddedAboveThreshold() {
        val trigger = LogoutReminderTrigger(enabled = true, thresholdSec = 60)
        assertTrue(trigger.update(active(60)))
        assertFalse(trigger.update(active(120)))
        assertTrue(trigger.update(active(55)))
    }

    @Test
    fun suppressedWhenLockedOrNoSessionThenReArms() {
        val trigger = LogoutReminderTrigger(enabled = true, thresholdSec = 60)
        assertTrue(trigger.update(active(30)))
        assertFalse(trigger.update(locked(0)))
        assertFalse(trigger.update(SessionTrackerState.NoSession))
        assertTrue(trigger.update(active(30)))
    }

    @Test
    fun newSessionReArms() {
        val trigger = LogoutReminderTrigger(enabled = true, thresholdSec = 60)
        assertTrue(trigger.update(active(60, "s-1")))
        assertFalse(trigger.update(active(59, "s-1")))
        assertTrue(trigger.update(active(60, "s-2")))
    }

    @Test
    fun disabledNeverFires() {
        val trigger = LogoutReminderTrigger(enabled = false, thresholdSec = 60)
        assertFalse(trigger.update(active(60)))
        assertFalse(trigger.update(active(5)))
    }

    private fun active(remainingSec: Long, sessionId: String = "s-1"): SessionTrackerState =
        SessionTrackerState(
            hasSession = true,
            sessionId = sessionId,
            stationId = "tab-01",
            deviceType = "tab",
            locked = false,
            effectiveRemainingSec = remainingSec,
            lastCreditSeqNo = 1,
            serverUpdatedAt = "2026-05-20T00:00:00.000Z"
        )

    private fun locked(remainingSec: Long, sessionId: String = "s-1"): SessionTrackerState =
        active(remainingSec, sessionId).copy(locked = true)
}
