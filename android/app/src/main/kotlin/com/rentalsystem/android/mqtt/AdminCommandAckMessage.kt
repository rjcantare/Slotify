package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class AdminCommandAckMessage(
    @SerialName("requestId") val requestId: String,
    @SerialName("stationId") val stationId: String,
    @SerialName("result") val result: String,
    @SerialName("detail") val detail: String
)
