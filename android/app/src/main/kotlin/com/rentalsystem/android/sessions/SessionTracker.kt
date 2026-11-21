package com.rentalsystem.android.sessions

import android.os.SystemClock
import com.rentalsystem.android.mqtt.SessionMessage
import java.time.Instant
import java.time.format.DateTimeParseException

// Mirrors RentalClient.Windows/Sessions/SessionTracker.cs.
//
// Anchored monotonic countdown mirror for the station's session.
//
// On apply():
//   1. Compute a one-shot wall-clock catch-up against the server's `updatedAt`
//      so we don't undercount time that elapsed during a disconnect.
//   2. Anchor (remainingAtAnchor, SystemClock.elapsedRealtime()).
//
// On getCurrentState():
//   - locked => return literal remainingSec (server is the authority while locked).
//   - unlocked => remainingAtAnchor - monotonic elapsed since the anchor.
//
// The wall-clock math happens ONCE per apply, never on the ongoing tick.
// SystemClock.elapsedRealtime() is the Android equivalent of Stopwatch.GetTimestamp()
// - monotonic in ms, including deep-sleep time.
//
// lastCreditSeqNo monotonic - strictly older messages rejected; equal accepted
// so a locked-flag transition without a new credit still applies.
class SessionTracker(
    private val elapsedRealtimeMs: () -> Long = { SystemClock.elapsedRealtime() },
    private val currentTimeMillis: () -> Long = { System.currentTimeMillis() }
) {

    private val gate = Any()

    @Volatile
    private var latest: SessionMessage? = null
    private var anchorElapsedMs: Long = 0L
    private var remainingAtAnchorSec: Long = 0L

    // Application-level listener (process-scoped). Set once in
    // RentalAndroidApplication.onCreate and never cleared; used for cross-cutting
    // state-driven concerns like releasing/engaging the kiosk Lock Task Mode
    // when the session goes active or locked.
    @Volatile
    var onAppStateChange: ((SessionTrackerState) -> Unit)? = null

    // Activity-level listener (re-attached on each onStart, cleared on onStop)
    // so a destroyed Activity does not receive UI updates after it stops.
    @Volatile
    var onStateChange: ((SessionTrackerState) -> Unit)? = null

    fun apply(message: SessionMessage) {
        val newState: SessionTrackerState
        synchronized(gate) {
            val current = latest
            // Monotonic seqno gate, scoped to the same session. If the server
            // restarts and re-mints a fresh session for this station (new
            // sessionId), `lastCreditSeqNo` legitimately resets to a low number
            // and we must accept the new authoritative state — otherwise the
            // tracker is permanently stuck on the pre-reset view and the
            // S28f credit-dedup realignment trigger never fires (reported
            // 2026-05-25 during the s28g bench).
            if (current != null &&
                message.sessionId == current.sessionId &&
                message.lastCreditSeqNo < current.lastCreditSeqNo
            ) {
                return
            }

            // S20.9.1 mirror: defend against broker re-delivering the same retained
            // message (e.g. on resubscribe after a reconnect). Data-class equality
            // catches byte-identical re-deliveries while still allowing equal-seqNo
            // "lock flag flipped" transitions (those messages differ structurally).
            if (current != null && message == current) {
                return
            }

            latest = message
            anchorElapsedMs = elapsedRealtimeMs()
            remainingAtAnchorSec = reconcileFromServerStamp(message)
            newState = computeStateLocked()
        }
        // Application handler first so any cross-cutting effects (kiosk policy
        // changes, foreground-bring-back on lock) land before the Activity's
        // UI render sees the new state — keeps the visible UI consistent with
        // the kiosk state Activity will be operating under.
        onAppStateChange?.invoke(newState)
        onStateChange?.invoke(newState)
    }

    fun getCurrentState(): SessionTrackerState {
        synchronized(gate) {
            return computeStateLocked()
        }
    }

    private fun reconcileFromServerStamp(message: SessionMessage): Long {
        // Server is authoritative while locked; remainingSec is the literal truth
        // at publish time - no catch-up.
        if (message.locked) {
            return message.remainingSec.toLong()
        }

        val serverStamp = try {
            Instant.parse(message.updatedAt)
        } catch (_: DateTimeParseException) {
            // Malformed updatedAt - best effort: skip the catch-up rather than risk
            // a wildly wrong subtraction. The ongoing monotonic tick still runs.
            return message.remainingSec.toLong()
        } catch (_: NullPointerException) {
            return message.remainingSec.toLong()
        }

        val deltaSec = (currentTimeMillis() / 1000) - serverStamp.epochSecond
        if (deltaSec <= 0) {
            // Server stamp is at or in the future of our wall clock (clock skew
            // or freshly published). No catch-up needed.
            return message.remainingSec.toLong()
        }
        return maxOf(0L, message.remainingSec.toLong() - deltaSec)
    }

    private fun computeStateLocked(): SessionTrackerState {
        val message = latest ?: return SessionTrackerState.NoSession

        val effective: Long = if (message.locked) {
            message.remainingSec.toLong()
        } else {
            val elapsedSec = (elapsedRealtimeMs() - anchorElapsedMs) / 1000
            maxOf(0L, remainingAtAnchorSec - elapsedSec)
        }

        return SessionTrackerState(
            hasSession = true,
            sessionId = message.sessionId,
            stationId = message.stationId,
            deviceType = message.deviceType,
            locked = message.locked,
            effectiveRemainingSec = effective,
            lastCreditSeqNo = message.lastCreditSeqNo,
            serverUpdatedAt = message.updatedAt
        )
    }
}
