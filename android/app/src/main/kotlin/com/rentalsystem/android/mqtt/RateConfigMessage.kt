package com.rentalsystem.android.mqtt

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class RateConfigMessage(
    @SerialName("version") val version: Int = 0,
    @SerialName("rates") val rates: Map<String, Map<String, Int>> = emptyMap(),
    @SerialName("addTimeRates") val addTimeRates: Map<String, Map<String, Int>> = emptyMap(),
    @SerialName("insertCoinRules") val insertCoinRules: Map<String, InsertCoinRateRuleMessage> = emptyMap(),
    @SerialName("updatedAt") val updatedAt: String = ""
)

@Serializable
data class InsertCoinRateRuleMessage(
    @SerialName("starterMaxPesos") val starterMaxPesos: Int = 0,
    @SerialName("starterSeconds") val starterSeconds: Int = 0,
    @SerialName("normalizedFromPesos") val normalizedFromPesos: Int = 0,
    @SerialName("perPesoSeconds") val perPesoSeconds: Int = 0
)
