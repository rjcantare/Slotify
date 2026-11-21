package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class ClaimEndMessage(
    @SerialName("requestId") val requestId: String,
    @SerialName("claimId") val claimId: String,
    @SerialName("stationId") val stationId: String,
    @SerialName("endedAt") val endedAt: String
)
