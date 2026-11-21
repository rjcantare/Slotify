package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class TabletAppInventoryMessage(
    @SerialName("stationId") val stationId: String,
    @SerialName("deviceType") val deviceType: String,
    @SerialName("publishedAt") val publishedAt: String,
    @SerialName("apps") val apps: List<TabletInstalledAppMessage>
)

@Serializable
data class TabletInstalledAppMessage(
    @SerialName("displayName") val displayName: String,
    @SerialName("packageName") val packageName: String,
    @SerialName("system") val system: Boolean
)
