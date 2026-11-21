package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class AdminCommandMessage(
    @SerialName("requestId") val requestId: String = "",
    @SerialName("command") val command: String = "",
    @SerialName("issuedAt") val issuedAt: String = "",
    @SerialName("stationId") val stationId: String = "",
    @SerialName("reason") val reason: String = ""
)
