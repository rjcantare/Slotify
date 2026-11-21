package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class TabletAppConfigMessage(
    @SerialName("version") val version: Int = 0,
    @SerialName("updatedAt") val updatedAt: String = "",
    @SerialName("apps") val apps: List<TabletAppConfigItemMessage> = emptyList()
)

@Serializable
data class TabletAppConfigItemMessage(
    @SerialName("displayName") val displayName: String = "",
    @SerialName("packageName") val packageName: String = "",
    @SerialName("enabled") val enabled: Boolean = false,
    @SerialName("deepFreeze") val deepFreeze: Boolean = false,
    @SerialName("sortOrder") val sortOrder: Int = 0,
    @SerialName("category") val category: String = ""
)
