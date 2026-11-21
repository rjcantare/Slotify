package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class CreditMessage(
    @SerialName("seqNo") val seqNo: Long = 0L,
    @SerialName("secondsToAdd") val secondsToAdd: Int = 0,
    @SerialName("source") val source: String = "",
    @SerialName("denomination") val denomination: Int? = null,
    @SerialName("issuedAt") val issuedAt: String = ""
)
