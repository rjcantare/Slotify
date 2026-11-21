package com.rentalsystem.android.claims

import com.rentalsystem.android.mqtt.ClaimResultMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ClaimTrackerTest {
    private var elapsedMs = 0L
    private val tracker = ClaimTracker(elapsedRealtimeMs = { elapsedMs })

    @Test
    fun grantTracksWindow() {
        // 2026-05-28 session: DoneButtonLockoutSec dropped to 0, so DONE
        // becomes interactive the instant the claim is granted.
        tracker.startPending("r-1")
        tracker.apply(
            ClaimResultMessage(
                requestId = "r-1",
                result = "granted",
                claimId = "c-1",
                windowSec = 60,
                acceptorId = "coin-01"
            )
        )

        assertTrue(tracker.getCurrentState().canShowDoneButton)

        elapsedMs += 5_000
        val state = tracker.getCurrentState()

        assertEquals(ClaimStatus.Granted, state.status)
        assertEquals(55, state.grantedRemainingSec)
        assertTrue(state.canShowDoneButton)
    }

    @Test
    fun mismatchedResultDoesNotOverwritePendingRequest() {
        tracker.startPending("r-1")
        tracker.apply(ClaimResultMessage(requestId = "other", result = "denied", reason = "slot_in_use"))

        assertEquals(ClaimStatus.Pending, tracker.getCurrentState().status)
    }

    @Test
    fun deniedClaimStoresReasonAndHeldBy() {
        tracker.startPending("r-1")
        tracker.apply(
            ClaimResultMessage(
                requestId = "r-1",
                result = "denied",
                reason = "slot_in_use",
                heldBy = "pc-01"
            )
        )

        val state = tracker.getCurrentState()
        assertEquals(ClaimStatus.Denied, state.status)
        assertEquals("slot_in_use", state.denyReason)
        assertEquals("pc-01", state.heldBy)
    }

    @Test
    fun runningCreditAccumulatesOnlyWhileGranted() {
        tracker.addRunningCoin(secondsToAdd = 100, denomination = 5)
        assertEquals(0, tracker.getCurrentState().runningTotalPesos)

        tracker.startPending("r-1")
        tracker.apply(ClaimResultMessage(requestId = "r-1", result = "granted", claimId = "c-1"))
        tracker.addRunningCoin(secondsToAdd = 0, denomination = 1)
        tracker.addRunningCoin(secondsToAdd = 1800, denomination = 5)

        val state = tracker.getCurrentState()
        assertEquals(1800, state.runningTotalSec)
        assertEquals(6, state.runningTotalPesos)
    }

    @Test
    fun markTimedOutOnlyAffectsGrantedClaim() {
        tracker.markTimedOut()
        assertEquals(ClaimStatus.Idle, tracker.getCurrentState().status)

        tracker.startPending("r-1")
        tracker.apply(ClaimResultMessage(requestId = "r-1", result = "granted", claimId = "c-1"))
        tracker.markTimedOut()

        assertEquals(ClaimStatus.TimedOut, tracker.getCurrentState().status)
    }

    @Test
    fun markOfflineTimeoutOnlyAffectsPendingClaim() {
        // From Idle: no-op
        tracker.markOfflineTimeout()
        assertEquals(ClaimStatus.Idle, tracker.getCurrentState().status)

        // From Granted: no-op (TimedOut handles that case)
        tracker.startPending("r-1")
        tracker.apply(ClaimResultMessage(requestId = "r-1", result = "granted", claimId = "c-1"))
        tracker.markOfflineTimeout()
        assertEquals(ClaimStatus.Granted, tracker.getCurrentState().status)

        // From Pending: transitions to OfflineTimeout (the watchdog fires)
        tracker.reset()
        tracker.startPending("r-2")
        tracker.markOfflineTimeout()
        assertEquals(ClaimStatus.OfflineTimeout, tracker.getCurrentState().status)
    }

    @Test
    fun lateClaimResultAfterOfflineTimeoutIsIgnored() {
        // Customer tapped Insert Coin, watchdog fired at 12 s, but a slow
        // server granted r-1 anyway 13 s later. Must NOT silently switch the
        // UI back to "Drop Coins" — the customer has already been told the
        // slot is offline and may have moved on.
        tracker.startPending("r-1")
        tracker.markOfflineTimeout()
        assertEquals(ClaimStatus.OfflineTimeout, tracker.getCurrentState().status)

        tracker.apply(ClaimResultMessage(requestId = "r-1", result = "granted", claimId = "c-late"))
        assertEquals(ClaimStatus.OfflineTimeout, tracker.getCurrentState().status)

        // Same for a late denied — also ignored.
        tracker.apply(ClaimResultMessage(requestId = "r-1", result = "denied", reason = "slot_in_use"))
        assertEquals(ClaimStatus.OfflineTimeout, tracker.getCurrentState().status)
    }
}
