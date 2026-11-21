package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class ClaimResultMessage(
    @SerialName("requestId") val requestId: String = "",
    @SerialName("result") val result: String = "",
    @SerialName("claimId") val claimId: String? = null,
    @SerialName("windowSec") val windowSec: Int? = null,
    @SerialName("acceptorId") val acceptorId: String? = null,
    @SerialName("reason") val reason: String? = null,
    @SerialName("heldBy") val heldBy: String? = null,
    @SerialName("banUntil") val banUntil: String? = null
)
