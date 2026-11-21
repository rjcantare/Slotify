package com.rentalsystem.android.audio

enum class CountdownWarningCueKind {
    FirstWarning,
    FinalCountdown
}

data class CountdownWarningCue(
    val kind: CountdownWarningCueKind,
    val remainingSec: Long
)
