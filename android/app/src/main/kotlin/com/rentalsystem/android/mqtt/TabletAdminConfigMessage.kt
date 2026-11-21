package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class TabletAdminConfigMessage(
    @SerialName("version") val version: Int = 0,
    @SerialName("updatedAt") val updatedAt: String = "",
    @SerialName("algorithm") val algorithm: String = "",
    @SerialName("passwordSalt") val passwordSalt: String = "",
    @SerialName("passwordHash") val passwordHash: String = ""
)
