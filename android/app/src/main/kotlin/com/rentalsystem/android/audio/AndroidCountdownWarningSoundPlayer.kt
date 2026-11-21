package com.rentalsystem.android.audio

import android.media.AudioManager
import android.media.ToneGenerator

class AndroidCountdownWarningSoundPlayer : CountdownWarningSoundPlayer {
    private val toneGenerator: ToneGenerator? = try {
        ToneGenerator(AudioManager.STREAM_MUSIC, ToneVolumePercent)
    } catch (_: Throwable) {
        null
    }

    override fun play(cue: CountdownWarningCue) {
        try {
            val tone = when {
                cue.kind == CountdownWarningCueKind.FirstWarning -> ToneGenerator.TONE_PROP_BEEP2
                cue.remainingSec <= UrgentFinalCountdownSec -> ToneGenerator.TONE_CDMA_ALERT_CALL_GUARD
                else -> ToneGenerator.TONE_PROP_BEEP
            }
            val durationMs = if (cue.kind == CountdownWarningCueKind.FirstWarning) {
                FirstWarningDurationMs
            } else {
                FinalCountdownDurationMs
            }
            toneGenerator?.startTone(tone, durationMs)
        } catch (_: Throwable) {
            // Best effort only; broken audio output must not break rental state.
        }
    }

    fun release() {
        try {
            toneGenerator?.release()
        } catch (_: Throwable) {
            // Nothing useful to recover during process teardown.
        }
    }

    private companion object {
        const val ToneVolumePercent = 85
        const val UrgentFinalCountdownSec = 3L
        const val FirstWarningDurationMs = 250
        const val FinalCountdownDurationMs = 120
    }
}
