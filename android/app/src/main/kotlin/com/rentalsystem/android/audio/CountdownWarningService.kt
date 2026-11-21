package com.rentalsystem.android.audio

import com.rentalsystem.android.config.CountdownWarningConfig
import com.rentalsystem.android.sessions.SessionTrackerState

class CountdownWarningService(
    private val config: CountdownWarningConfig,
    private val soundPlayer: CountdownWarningSoundPlayer
) {
    private val finalCountdownSecondsPlayed = mutableSetOf<Long>()

    private var sessionId: String? = null
    private var firstWarningPlayed = false

    fun update(state: SessionTrackerState): CountdownWarningCue? {
        val cue = getCue(state)
        if (cue != null) {
            try {
                soundPlayer.play(cue)
            } catch (_: Throwable) {
                // Audio failures must never affect kiosk/session enforcement.
            }
        }
        return cue
    }

    private fun getCue(state: SessionTrackerState): CountdownWarningCue? {
        if (!config.enabled || !state.hasSession || state.locked || state.effectiveRemainingSec <= 0) {
            reset()
            return null
        }

        if (sessionId != state.sessionId) {
            reset()
            sessionId = state.sessionId
        }

        val remaining = state.effectiveRemainingSec

        if (remaining > config.firstWarningSec) {
            firstWarningPlayed = false
        }

        if (remaining > config.finalCountdownStartSec) {
            finalCountdownSecondsPlayed.clear()
        }

        if (remaining <= config.finalCountdownStartSec) {
            return if (finalCountdownSecondsPlayed.add(remaining)) {
                CountdownWarningCue(CountdownWarningCueKind.FinalCountdown, remaining)
            } else {
                null
            }
        }

        if (remaining <= config.firstWarningSec && !firstWarningPlayed) {
            firstWarningPlayed = true
            return CountdownWarningCue(CountdownWarningCueKind.FirstWarning, remaining)
        }

        return null
    }

    private fun reset() {
        sessionId = null
        firstWarningPlayed = false
        finalCountdownSecondsPlayed.clear()
    }
}
