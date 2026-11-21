package com.rentalsystem.android.claims

enum class ClaimStatus {
    Idle,
    Pending,
    Granted,
    Denied,
    TimedOut,
    // S28e: Pending watchdog fired before claim/result ever arrived. Distinct
    // from TimedOut (which is "Granted window expired without coins") — this
    // means the server never responded at all. Triggered by the 12 s timer
    // started in RentalAndroidApplication when the tracker enters Pending.
    OfflineTimeout
}

data class ClaimTrackerState(
    val status: ClaimStatus,
    val requestId: String?,
    val claimId: String?,
    val acceptorId: String?,
    val windowSec: Long,
    val elapsedSec: Long,
    val denyReason: String?,
    val heldBy: String?,
    val runningTotalSec: Long,
    val runningTotalPesos: Int,
    val banUntil: String? = null
) {
    val grantedRemainingSec: Long
        get() = maxOf(0L, windowSec - elapsedSec)

    val canShowDoneButton: Boolean
        get() = status == ClaimStatus.Granted && elapsedSec >= DoneButtonLockoutSec

    companion object {
        const val DoneButtonLockoutSec = 0L

        val Idle: ClaimTrackerState = ClaimTrackerState(
            status = ClaimStatus.Idle,
            requestId = null,
            claimId = null,
            acceptorId = null,
            windowSec = 0,
            elapsedSec = 0,
            denyReason = null,
            heldBy = null,
            runningTotalSec = 0,
            runningTotalPesos = 0
        )
    }
}
