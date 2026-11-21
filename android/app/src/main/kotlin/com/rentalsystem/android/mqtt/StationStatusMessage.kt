package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// Mirrors RentalShared/Mqtt/StationStatusMessage.cs.
// Retained on `rental/station/{stationId}/status` and registered as the LWT.
// Online payload carries deviceType/appVersion/uptime; LWT payload is fixed at
// CONNECT and only carries `online: false` + `deviceId` (no timestamp — the
// server stamps wall-clock receipt time itself).
//
// Nullable fields are encoded only when present (encodeDefaults=false on the
// json instance used at the call site); this matches the C# serializer's
// DefaultIgnoreCondition.WhenWritingNull behaviour.
@Serializable
data class StationStatusMessage(
    @SerialName("online") val online: Boolean,
    @SerialName("deviceId") val deviceId: String? = null,
    @SerialName("deviceType") val deviceType: String? = null,
    @SerialName("appVersion") val appVersion: String? = null,
    @SerialName("uptimeSec") val uptimeSec: Long? = null
)
