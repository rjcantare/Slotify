package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class AbusePreventionConfigMessage(
    @SerialName("enabled") val enabled: Boolean = false,
    @SerialName("maxEmptyClaims") val maxEmptyClaims: Int = 3,
    @SerialName("banMinutes") val banMinutes: Int = 2,
    @SerialName("updatedAt") val updatedAt: String = ""
)
