package com.rentalsystem.android.sessions

// Mirrors RentalClient.Windows/Sessions/SessionTrackerState.cs.
data class SessionTrackerState(
    val hasSession: Boolean,
    val sessionId: String,
    val stationId: String,
    val deviceType: String,
    val locked: Boolean,
    val effectiveRemainingSec: Long,
    val lastCreditSeqNo: Long,
    val serverUpdatedAt: String
) {
    /**
     * `HH:MM:SS` formatted countdown for the UI. Matches the C# `FormatRemaining()`
     * output for direct visual parity with the Windows client.
     */
    fun formatRemaining(): String {
        val hours = effectiveRemainingSec / 3600
        val minutes = (effectiveRemainingSec % 3600) / 60
        val seconds = effectiveRemainingSec % 60
        return "%02d:%02d:%02d".format(hours, minutes, seconds)
    }

    companion object {
        val NoSession: SessionTrackerState = SessionTrackerState(
            hasSession = false,
            sessionId = "",
            stationId = "",
            deviceType = "",
            locked = false,
            effectiveRemainingSec = 0,
            lastCreditSeqNo = 0,
            serverUpdatedAt = ""
        )
    }
}
