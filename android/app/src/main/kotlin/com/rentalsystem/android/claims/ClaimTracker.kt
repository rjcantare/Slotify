package com.rentalsystem.android.claims

import android.os.SystemClock
import com.rentalsystem.android.mqtt.ClaimResultMessage

// Android mirror of the Windows ClaimTracker. It is passive: MainActivity starts
// Pending when it publishes claim/request, then server claim/result drives the
// granted or denied state. The granted window ticks from elapsedRealtime().
class ClaimTracker(
    private val elapsedRealtimeMs: () -> Long = { SystemClock.elapsedRealtime() }
) {

    private val gate = Any()

    private var status: ClaimStatus = ClaimStatus.Idle
    private var requestId: String? = null
    private var claimId: String? = null
    private var acceptorId: String? = null
    private var windowSec: Long = 0
    private var grantedAtElapsedMs: Long = 0
    private var denyReason: String? = null
    private var heldBy: String? = null
    private var banUntil: String? = null
    private var runningTotalSec: Long = 0
    private var runningTotalPesos: Int = 0

    // Application-level listener (process-scoped). Set once in
    // RentalAndroidApplication.onCreate; drives the Pending watchdog so a
    // server that never responds to claim/request doesn't strand the UI
    // on "Requesting..." forever. Mirrors SessionTracker's two-listener
    // pattern — onAppStateChange for cross-cutting effects, onStateChange
    // for the foreground Activity's UI render.
    @Volatile
    var onAppStateChange: ((ClaimTrackerState) -> Unit)? = null

    @Volatile
    var onStateChange: ((ClaimTrackerState) -> Unit)? = null

    fun startPending(requestId: String) {
        synchronized(gate) {
            status = ClaimStatus.Pending
            this.requestId = requestId
            resetGrantLocked()
            resetDenyLocked()
        }
        emit()
    }

    fun apply(result: ClaimResultMessage) {
        synchronized(gate) {
            // OfflineTimeout is terminal in the same way TimedOut is — a late
            // grant arriving after the watchdog fired must not silently flip
            // the UI back to Granted, because the user has already been told
            // "coin slot offline" and may have moved on. They must explicitly
            // tap Insert Coin again to start a fresh claim.
            if ((status == ClaimStatus.Idle || status == ClaimStatus.TimedOut || status == ClaimStatus.OfflineTimeout) ||
                requestId != result.requestId
            ) {
                return
            }

            requestId = result.requestId
            when (result.result) {
                "granted" -> {
                    if (status == ClaimStatus.Granted && claimId == result.claimId) {
                        // Window-extension refresh from a coin drop — re-anchor
                        // the countdown without resetting running totals.
                        windowSec = result.windowSec?.toLong() ?: 60L
                        grantedAtElapsedMs = elapsedRealtimeMs()
                        resetDenyLocked()
                    } else {
                        status = ClaimStatus.Granted
                        claimId = result.claimId
                        acceptorId = result.acceptorId
                        windowSec = result.windowSec?.toLong() ?: 60L
                        grantedAtElapsedMs = elapsedRealtimeMs()
                        runningTotalSec = 0
                        runningTotalPesos = 0
                        resetDenyLocked()
                    }
                }
                "denied" -> {
                    status = ClaimStatus.Denied
                    denyReason = result.reason
                    heldBy = result.heldBy
                    banUntil = result.banUntil
                    resetGrantLocked()
                }
            }
        }
        emit()
    }

    fun markTimedOut() {
        synchronized(gate) {
            if (status != ClaimStatus.Granted) {
                return
            }
            status = ClaimStatus.TimedOut
        }
        emit()
    }

    fun markOfflineTimeout() {
        synchronized(gate) {
            if (status != ClaimStatus.Pending) {
                return
            }
            status = ClaimStatus.OfflineTimeout
        }
        emit()
    }

    fun reset() {
        synchronized(gate) {
            status = ClaimStatus.Idle
            requestId = null
            resetGrantLocked()
            resetDenyLocked()
        }
        emit()
    }

    fun getCurrentState(): ClaimTrackerState {
        synchronized(gate) {
            return computeStateLocked()
        }
    }

    fun addRunningCoin(secondsToAdd: Int, denomination: Int?) {
        if (secondsToAdd < 0) {
            return
        }

        synchronized(gate) {
            if (status != ClaimStatus.Granted) {
                return
            }
            runningTotalSec += secondsToAdd
            if (denomination != null && denomination > 0) {
                runningTotalPesos += denomination
            }
        }
        emit()
    }

    private fun computeStateLocked(): ClaimTrackerState {
        val elapsedSec = if (status == ClaimStatus.Granted) {
            (elapsedRealtimeMs() - grantedAtElapsedMs) / 1000
        } else {
            0L
        }

        return ClaimTrackerState(
            status = status,
            requestId = requestId,
            claimId = claimId,
            acceptorId = acceptorId,
            windowSec = windowSec,
            elapsedSec = elapsedSec,
            denyReason = denyReason,
            heldBy = heldBy,
            runningTotalSec = runningTotalSec,
            runningTotalPesos = runningTotalPesos,
            banUntil = banUntil
        )
    }

    private fun resetGrantLocked() {
        claimId = null
        acceptorId = null
        windowSec = 0
        grantedAtElapsedMs = 0
        runningTotalSec = 0
        runningTotalPesos = 0
    }

    private fun resetDenyLocked() {
        denyReason = null
        heldBy = null
        banUntil = null
    }

    private fun emit() {
        val snapshot = getCurrentState()
        // Application handler first so the Pending watchdog start/cancel
        // happens before the Activity's UI render reads the new state.
        onAppStateChange?.invoke(snapshot)
        onStateChange?.invoke(snapshot)
    }
}
