package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// Wire shape of the transient `rental/station/{stationId}/apps/reset` command.
// Server -> station: staff picked one or more apps to log out / reset on this
// tablet. The tablet clears each listed package's data via Device Owner
// clearApplicationUserData (subject to protected-package + own-package guards).
@Serializable
data class AppResetCommandMessage(
    @SerialName("requestId") val requestId: String = "",
    @SerialName("stationId") val stationId: String = "",
    @SerialName("packages") val packages: List<String> = emptyList()
)
