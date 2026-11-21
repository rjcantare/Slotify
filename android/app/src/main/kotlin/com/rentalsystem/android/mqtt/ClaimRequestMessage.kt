package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class ClaimRequestMessage(
    @SerialName("requestId") val requestId: String,
    @SerialName("stationId") val stationId: String,
    @SerialName("requestedAt") val requestedAt: String
)
