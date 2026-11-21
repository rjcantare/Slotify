package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class CreditAckMessage(
    @SerialName("seqNo") val seqNo: Long,
    @SerialName("stationId") val stationId: String
)
