package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// Mirrors RentalShared/Mqtt/SessionMessage.cs.
//
// Wire shape of the retained `rental/station/{stationId}/session` payload.
// Stations treat this as truth on reconnect and reconcile locally between
// updates; the server only publishes on state transitions (start, credit,
// lock, end).
@Serializable
data class SessionMessage(
    @SerialName("sessionId") val sessionId: String = "",
    @SerialName("stationId") val stationId: String = "",
    @SerialName("deviceType") val deviceType: String = "",
    @SerialName("locked") val locked: Boolean = false,
    @SerialName("remainingSec") val remainingSec: Int = 0,
    @SerialName("lastCreditSeqNo") val lastCreditSeqNo: Long = 0L,
    @SerialName("updatedAt") val updatedAt: String = ""
)
