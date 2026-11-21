package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class UpdateManifest(
    @SerialName("version") val version: String = "",
    @SerialName("publishedAt") val publishedAt: String = "",
    @SerialName("server") val server: UpdateComponent? = null,
    @SerialName("client") val client: UpdateComponent? = null,
    @SerialName("android") val android: UpdateComponent? = null
)

@Serializable
data class UpdateComponent(
    @SerialName("version") val version: String = "",
    @SerialName("url") val url: String = "",
    @SerialName("sha256") val sha256: String = "",
    @SerialName("sizeBytes") val sizeBytes: Long = 0
)
