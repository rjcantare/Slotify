package com.rentalsystem.android.config

import android.content.Context
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

// Mirrors RentalClient.Windows/Configuration/ClientConfig.cs.
// Loaded from assets/appsettings.json at startup. Per-device overrides are
// expected to be made by editing this file and rebuilding the APK (the asset
// is baked into the APK; Android does not have the .NET "appsettings.Local.json"
// overlay pattern available to read at runtime).
//
// device_type enum is locked by the MQTT protocol + DB schema: "pc", "phn", "tab".
@Serializable
data class ClientConfig(
    @SerialName("stationId") val stationId: String = "",
    @SerialName("deviceType") val deviceType: String = "",
    @SerialName("appVersion") val appVersion: String = "",
    @SerialName("broker") val broker: BrokerConfig = BrokerConfig(),
    @SerialName("countdownWarning") val countdownWarning: CountdownWarningConfig = CountdownWarningConfig(),
    @SerialName("brightness") val brightness: BrightnessConfig = BrightnessConfig(),
    @SerialName("freshUserReset") val freshUserReset: FreshUserResetConfig = FreshUserResetConfig()
) {
    fun validate() {
        require(stationId.isNotBlank()) { "Client config: stationId is required." }
        require(deviceType in listOf("pc", "phn", "tab")) {
            "Client config: deviceType must be 'pc', 'phn', or 'tab'; got '$deviceType'."
        }
        require(broker.host.isNotBlank()) { "Client config: broker.host is required." }
        require(broker.port in 1..65535) {
            "Client config: broker.port must be 1..65535; got ${broker.port}."
        }
        countdownWarning.validate()
        brightness.validate()
        freshUserReset.validate(packageName = "freshUserReset")
    }

    companion object {
        private const val ASSET_NAME = "appsettings.json"

        // Lenient JSON: server may add fields ahead of the Android client; ignore them
        // rather than fail to parse (forward-compatible per MQTT Protocol §Conventions).
        private val json = Json {
            ignoreUnknownKeys = true
            isLenient = true
        }

        fun loadFromAssets(context: Context): ClientConfig {
            val raw = context.assets.open(ASSET_NAME).bufferedReader().use { it.readText() }
            val config = json.decodeFromString(serializer(), raw)
            config.validate()
            return config
        }
    }
}

@Serializable
data class BrokerConfig(
    @SerialName("host") val host: String = "127.0.0.1",
    @SerialName("port") val port: Int = 1883,
    @SerialName("username") val username: String? = null,
    @SerialName("password") val password: String? = null
) {
    val hasCredentials: Boolean
        get() = !username.isNullOrBlank() && !password.isNullOrBlank()

    companion object {
        const val AUTO_HOST_SENTINEL = "auto"
    }
}

@Serializable
data class CountdownWarningConfig(
    @SerialName("enabled") val enabled: Boolean = true,
    @SerialName("firstWarningSec") val firstWarningSec: Int = 60,
    @SerialName("finalCountdownStartSec") val finalCountdownStartSec: Int = 10,
    @SerialName("logoutReminderEnabled") val logoutReminderEnabled: Boolean = true,
    @SerialName("logoutReminderMessage") val logoutReminderMessage: String =
        "Time almost up - log out of your game accounts (Mobile Legends, Facebook, Google) and save your progress before your time ends."
) {
    fun validate(packageName: String = "countdownWarning") {
        require(firstWarningSec > 0) {
            "$packageName.firstWarningSec must be > 0; got $firstWarningSec."
        }
        require(finalCountdownStartSec > 0) {
            "$packageName.finalCountdownStartSec must be > 0; got $finalCountdownStartSec."
        }
        require(firstWarningSec > finalCountdownStartSec) {
            "$packageName.firstWarningSec must be greater than $packageName.finalCountdownStartSec."
        }
    }
}

@Serializable
data class BrightnessConfig(
    @SerialName("enabled") val enabled: Boolean = true,
    @SerialName("defaultPercent") val defaultPercent: Int = 75,
    @SerialName("minPercent") val minPercent: Int = 20,
    @SerialName("maxPercent") val maxPercent: Int = 100,
    @SerialName("stepPercent") val stepPercent: Int = 10
) {
    fun validate(packageName: String = "brightness") {
        require(minPercent in 1..100) {
            "$packageName.minPercent must be 1..100; got $minPercent."
        }
        require(maxPercent in minPercent..100) {
            "$packageName.maxPercent must be minPercent..100; got $maxPercent."
        }
        require(defaultPercent in minPercent..maxPercent) {
            "$packageName.defaultPercent must be minPercent..maxPercent; got $defaultPercent."
        }
        require(stepPercent in 1..(maxPercent - minPercent).coerceAtLeast(1)) {
            "$packageName.stepPercent must be positive and fit the configured range; got $stepPercent."
        }
    }
}

@Serializable
data class FreshUserResetConfig(
    @SerialName("enabled") val enabled: Boolean = false,
    @SerialName("customerUserModeEnabled") val customerUserModeEnabled: Boolean = false,
    @SerialName("clearApplicationDataPackages") val clearApplicationDataPackages: List<String> = emptyList()
) {
    fun validate(packageName: String = "freshUserReset") {
        require(clearApplicationDataPackages.none { it.isBlank() }) {
            "$packageName.clearApplicationDataPackages cannot contain blank package names."
        }
    }
}
