package com.rentalsystem.android.brightness

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.provider.Settings
import android.view.Window
import android.view.WindowManager
import com.rentalsystem.android.RentalDeviceAdminReceiver
import com.rentalsystem.android.config.BrightnessConfig
import kotlin.math.roundToInt

class BrightnessController(
    context: Context,
    private val config: BrightnessConfig
) {
    private val appContext = context.applicationContext
    private val policy = appContext.getSystemService(DevicePolicyManager::class.java)
    private val admin = ComponentName(appContext, RentalDeviceAdminReceiver::class.java)
    private val preferences = context.applicationContext.getSharedPreferences(
        PreferencesName,
        Context.MODE_PRIVATE
    )

    val isEnabled: Boolean
        get() = config.enabled

    var currentPercent: Int = BrightnessLevel.clampPercent(
        preferences.getInt(KeyPercent, config.defaultPercent),
        config.minPercent,
        config.maxPercent
    )
        private set

    fun applyTo(window: Window) {
        val attributes = window.attributes
        attributes.screenBrightness = if (config.enabled) {
            BrightnessLevel.toWindowBrightness(currentPercent, config.minPercent, config.maxPercent)
        } else {
            WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        }
        window.attributes = attributes

        if (config.enabled) {
            applySystemBrightnessIfPolicyOwner()
        }
    }

    fun increase(): Int = setPercent(
        BrightnessLevel.stepPercent(
            currentPercent,
            direction = 1,
            minPercent = config.minPercent,
            maxPercent = config.maxPercent,
            stepPercent = config.stepPercent
        )
    )

    fun decrease(): Int = setPercent(
        BrightnessLevel.stepPercent(
            currentPercent,
            direction = -1,
            minPercent = config.minPercent,
            maxPercent = config.maxPercent,
            stepPercent = config.stepPercent
        )
    )

    fun label(): String =
        BrightnessLevel.label(currentPercent, config.minPercent, config.maxPercent)

    private fun setPercent(percent: Int): Int {
        currentPercent = BrightnessLevel.clampPercent(percent, config.minPercent, config.maxPercent)
        preferences.edit().putInt(KeyPercent, currentPercent).apply()
        return currentPercent
    }

    private fun applySystemBrightnessIfPolicyOwner() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
            return
        }

        val ownsPolicy = policy.isDeviceOwnerApp(appContext.packageName) ||
            policy.isProfileOwnerApp(appContext.packageName)
        if (!ownsPolicy) {
            return
        }

        try {
            policy.setSystemSetting(
                admin,
                Settings.System.SCREEN_BRIGHTNESS_MODE,
                Settings.System.SCREEN_BRIGHTNESS_MODE_MANUAL.toString()
            )
            policy.setSystemSetting(
                admin,
                Settings.System.SCREEN_BRIGHTNESS,
                toSystemBrightness(currentPercent).toString()
            )
        } catch (_: SecurityException) {
            // The per-window override above still works if OEM policy blocks
            // system setting writes in a given provisioning state.
        } catch (_: IllegalArgumentException) {
            // Same fallback: keep the kiosk window controlled.
        }
    }

    private fun toSystemBrightness(percent: Int): Int {
        val clamped = BrightnessLevel.clampPercent(percent, config.minPercent, config.maxPercent)
        return ((clamped / 100f) * MaxSystemBrightness).roundToInt().coerceIn(1, MaxSystemBrightness)
    }

    private companion object {
        const val PreferencesName = "rental_brightness"
        const val KeyPercent = "brightness_percent"
        const val MaxSystemBrightness = 255
    }
}
