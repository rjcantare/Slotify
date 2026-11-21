package com.rentalsystem.android

import android.Manifest
import android.app.Activity
import android.app.ActivityManager
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController

data class KioskState(
    val isDeviceOwner: Boolean,
    val isProfileOwner: Boolean,
    val isAdminActive: Boolean,
    val isLockTaskPermitted: Boolean,
    val isLockTaskActive: Boolean,
    val policyNotes: List<String>
)

class KioskController(private val activity: Activity) {
    private val policy = activity.getSystemService(DevicePolicyManager::class.java)
    private val activityManager = activity.getSystemService(ActivityManager::class.java)
    private val admin = ComponentName(activity, RentalDeviceAdminReceiver::class.java)
    private val ownPackage = activity.packageName

    // S50b OTA: package names of common system installers, included in every
    // Lock Task allowlist so ACTION_VIEW with vnd.android.package-archive
    // launches its installer dialog without requiring admin-unlock first.
    // Android silently ignores entries that aren't installed on this device,
    // so listing all known OEMs is safe. Add new OEMs as they're encountered.
    private val installerPackages = listOf(
        "com.miui.packageinstaller",            // Xiaomi / HyperOS
        "com.google.android.packageinstaller",  // Stock Android (modern)
        "com.android.packageinstaller",         // Stock Android (legacy AOSP)
        "com.samsung.android.packageinstaller", // Samsung
        "com.huawei.packageinstaller"           // Huawei
    )

    fun configureAndStart(): KioskState {
        val notes = mutableListOf<String>()
        val isPolicyOwner = isPolicyOwner()

        if (!isPolicyOwner) {
            // No Device Owner — nothing to lock. Make sure system bars are
            // VISIBLE so the user can press Home and leave the rental app
            // normally. Reported 2026-05-23: after the Admin unlock cleared
            // DO, the user still saw a "locked" tablet because
            // configureAndStart blindly called hideSystemBars() on the next
            // onResume tick, hiding the status + nav bars and trapping the
            // user inside the rental app despite Lock Task being OFF.
            notes += "Device/Profile Owner is required before kiosk policies can be applied."
            showSystemBars()
            return readState(notes)
        }

        grantNotificationPermission(notes)
        applyPolicy(notes, "Lock task allowlist") {
            policy.setLockTaskPackages(admin, (listOf(ownPackage) + installerPackages).toTypedArray())
        }
        applyPolicy(notes, "Disable lock task system UI") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
            }
        }
        applyPolicy(notes, "Disable status shade") {
            val disabled = policy.setStatusBarDisabled(admin, true)
            if (!disabled) {
                notes += "Status shade policy was not accepted by this device."
            }
        }
        setHomeAliasEnabled(true)
        applyPolicy(notes, "Set kiosk as Home") {
            setPersistentHomeActivity()
        }

        val isLockTaskPermitted = policy.isLockTaskPermitted(ownPackage)
        if (isLockTaskPermitted && !isLockTaskActive()) {
            applyPolicy(notes, "Start lock task") {
                activity.startLockTask()
            }
        }

        hideSystemBars()

        return readState(notes)
    }

    /**
     * True when the kiosk is currently locking the device down. Used by
     * MainActivity.onWindowFocusChanged to decide whether to re-hide the
     * system bars on focus changes — after the Admin unlock clears DO,
     * the bars should stay visible so the user can leave the rental app.
     */
    fun isKioskActiveNow(): Boolean = isPolicyOwner() && isLockTaskActive()

    fun exitKioskForAdmin(): KioskState {
        val notes = mutableListOf<String>()

        if (!isPolicyOwner()) {
            notes += "Device Owner is not active; nothing to unlock."
            return readState(notes)
        }

        applyPolicy(notes, "Enable status shade") {
            val enabled = policy.setStatusBarDisabled(admin, false)
            if (!enabled) {
                notes += "Status shade policy was not accepted by this device."
            }
        }
        applyPolicy(notes, "Reset lock task system UI") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
            }
        }
        if (isLockTaskActive()) {
            applyPolicy(notes, "Stop lock task") {
                activity.stopLockTask()
            }
        }
        applyPolicy(notes, "Remove lock task allowlist") {
            policy.setLockTaskPackages(admin, emptyArray())
        }
        applyPolicy(notes, "Clear kiosk Home") {
            policy.clearPackagePersistentPreferredActivities(admin, ownPackage)
        }
        // S27: disable the HomeAlias so the rental app stops being a HOME
        // candidate on admin unlock. Otherwise Android can still route the
        // Home button back to the rental app.
        setHomeAliasEnabled(false)
        @Suppress("DEPRECATION")
        applyPolicy(notes, "Clear Device Owner") {
            policy.clearDeviceOwnerApp(ownPackage)
        }

        showSystemBars()
        if (notes.none { it.contains(" failed:") }) {
            notes += "Admin unlock: kiosk policies and Device Owner removed."
        }
        return readState(notes)
    }

    fun enterAdminMaintenanceMode(): KioskState {
        val notes = mutableListOf<String>()

        if (!isPolicyOwner()) {
            notes += "Device Owner is not active; use ADB/provisioning before kiosk can return."
            showSystemBars()
            return readState(notes)
        }

        applyPolicy(notes, "Reset lock task system UI") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
            }
        }
        applyPolicy(notes, "Enable status shade") {
            val enabled = policy.setStatusBarDisabled(admin, false)
            if (!enabled) {
                notes += "Status shade policy was not accepted by this device."
            }
        }
        if (isLockTaskActive()) {
            applyPolicy(notes, "Stop lock task") {
                activity.stopLockTask()
            }
        }
        applyPolicy(notes, "Remove lock task allowlist") {
            policy.setLockTaskPackages(admin, emptyArray())
        }
        applyPolicy(notes, "Clear kiosk Home") {
            policy.clearPackagePersistentPreferredActivities(admin, ownPackage)
        }
        setHomeAliasEnabled(false)

        showSystemBars()
        if (notes.none { it.contains(" failed:") }) {
            notes += "Admin maintenance: Device Owner kept; reopen Rental to return to kiosk."
        }
        return readState(notes)
    }

    fun readState(policyNotes: List<String> = emptyList()): KioskState {
        return KioskState(
            isDeviceOwner = policy.isDeviceOwnerApp(ownPackage),
            isProfileOwner = policy.isProfileOwnerApp(ownPackage),
            isAdminActive = policy.isAdminActive(admin),
            isLockTaskPermitted = policy.isLockTaskPermitted(ownPackage),
            isLockTaskActive = isLockTaskActive(),
            policyNotes = policyNotes
        )
    }

    fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            @Suppress("DEPRECATION")
            activity.window.setDecorFitsSystemWindows(false)
            activity.window.insetsController?.let { controller ->
                controller.hide(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
                controller.systemBarsBehavior =
                    WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
            return
        }

        @Suppress("DEPRECATION")
        activity.window.decorView.systemUiVisibility =
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE
    }

    fun showSystemBars() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            @Suppress("DEPRECATION")
            activity.window.setDecorFitsSystemWindows(true)
            activity.window.insetsController?.let { controller ->
                // Reset bars behavior to default BEFORE show() — hideSystemBars
                // sets BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE, which would cause
                // show() to render the bars transiently and auto-hide them
                // again. Without this reset, the user reports "no status bar"
                // after admin unlock even though show() was called.
                controller.systemBarsBehavior = WindowInsetsController.BEHAVIOR_DEFAULT
                controller.show(WindowInsets.Type.statusBars() or WindowInsets.Type.navigationBars())
            }
            return
        }

        @Suppress("DEPRECATION")
        activity.window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE
    }

    /**
     * S27: customer is paying — let them use the tablet. Releases Lock Task
     * Mode, re-enables the status shade, clears the persistent-Home preference
     * so the system launcher takes over when Home is pressed, and explicitly
     * shows the system bars. Device Owner stays set so a future locked-tick
     * can re-engage the kiosk via [engageKioskForLockedSession].
     */
    fun releaseKioskForActiveSession(): KioskState {
        val notes = mutableListOf<String>()

        if (!isPolicyOwner()) {
            notes += "Device Owner is not active — nothing to release."
            showSystemBars()
            return readState(notes)
        }

        grantNotificationPermission(notes)
        applyPolicy(notes, "Reset lock task system UI") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
            }
        }
        applyPolicy(notes, "Enable status shade") {
            policy.setStatusBarDisabled(admin, false)
        }
        if (isLockTaskActive()) {
            applyPolicy(notes, "Stop lock task") {
                activity.stopLockTask()
            }
        }
        applyPolicy(notes, "Clear kiosk Home preference for active session") {
            policy.clearPackagePersistentPreferredActivities(admin, ownPackage)
        }
        // S27: disable the HomeAlias so the rental app stops being a HOME
        // intent target. Combined with the cleared persistent preference
        // above, pressing Home now routes to the system launcher.
        setHomeAliasEnabled(false)

        showSystemBars()
        notes += "Active session: kiosk released so the customer can use the tablet"
        return readState(notes)
    }

    fun engageCustomerLauncherForActiveSession(allowedPackages: Collection<String>): KioskState {
        val notes = mutableListOf<String>()

        if (!isPolicyOwner()) {
            notes += "Device Owner is required before customer launcher kiosk can be applied."
            return readState(notes)
        }

        val allowlist = (sequenceOf(ownPackage) + allowedPackages.asSequence() + installerPackages.asSequence())
            .map { it.trim() }
            .filter { it.isNotBlank() }
            .distinct()
            .toList()

        grantNotificationPermission(notes)
        applyPolicy(notes, "Lock task customer app allowlist") {
            policy.setLockTaskPackages(admin, allowlist.toTypedArray())
        }
        applyPolicy(notes, "Enable customer Home escape") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_HOME)
            }
        }
        applyPolicy(notes, "Disable status shade") {
            val disabled = policy.setStatusBarDisabled(admin, true)
            if (!disabled) {
                notes += "Status shade policy was not accepted by this device."
            }
        }
        setHomeAliasEnabled(true)
        applyPolicy(notes, "Set rental launcher as Home") {
            setPersistentHomeActivity()
        }
        if (policy.isLockTaskPermitted(ownPackage) && !isLockTaskActive()) {
            applyPolicy(notes, "Start lock task") {
                activity.startLockTask()
            }
        }

        hideSystemBars()
        notes += "Active session: customer launcher kiosk allows ${allowlist.size - 1} app(s)"
        return readState(notes)
    }

    /**
     * S27: time is out (or new session locked from the start). Re-engages the
     * kiosk policies and brings the rental app back to the front of the
     * customer's experience: persistent Home, hidden status shade, Lock Task
     * Mode locked to this package.
     */
    fun engageKioskForLockedSession(): KioskState {
        val notes = mutableListOf<String>()

        if (!isPolicyOwner()) {
            notes += "Device Owner is required before kiosk policies can be applied."
            return readState(notes)
        }

        grantNotificationPermission(notes)
        applyPolicy(notes, "Lock task allowlist") {
            policy.setLockTaskPackages(admin, (listOf(ownPackage) + installerPackages).toTypedArray())
        }
        applyPolicy(notes, "Disable lock task system UI") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
            }
        }
        applyPolicy(notes, "Disable status shade") {
            val disabled = policy.setStatusBarDisabled(admin, true)
            if (!disabled) {
                notes += "Status shade policy was not accepted by this device."
            }
        }
        // S27: re-enable the HomeAlias before setPersistentHomeActivity so
        // the alias is a valid HOME target by the time DPM pins it. Without
        // this, setPersistentPreferredActivity can resolve to no component
        // (alias disabled by a prior active-session release).
        setHomeAliasEnabled(true)
        applyPolicy(notes, "Set kiosk as Home") {
            setPersistentHomeActivity()
        }
        if (policy.isLockTaskPermitted(ownPackage) && !isLockTaskActive()) {
            applyPolicy(notes, "Start lock task") {
                activity.startLockTask()
            }
        }

        hideSystemBars()
        notes += "Locked session: kiosk engaged"
        return readState(notes)
    }

    /**
     * S27: drives the kiosk state from the session state. Pass `true` when
     * the customer has remaining time (Add Time or unlocked active session)
     * and the kiosk should release; pass `false` when locked / out of time
     * and the kiosk should re-engage.
     */
    fun applySessionState(
        sessionActive: Boolean,
        allowedPackages: Collection<String> = emptyList()
    ): KioskState =
        if (sessionActive) {
            engageCustomerLauncherForActiveSession(allowedPackages)
        } else {
            engageKioskForLockedSession()
        }

    private fun setPersistentHomeActivity() {
        val filter = IntentFilter(Intent.ACTION_MAIN).apply {
            addCategory(Intent.CATEGORY_HOME)
            addCategory(Intent.CATEGORY_DEFAULT)
        }

        // S27: point the persistent preference at the HomeAlias, not
        // MainActivity directly. The alias is the only component declaring
        // CATEGORY.HOME in the manifest, and toggling it on/off at runtime
        // is what gates whether pressing Home routes to the rental app or
        // to the system launcher.
        policy.addPersistentPreferredActivity(
            admin,
            filter,
            ComponentName(activity, HomeAliasComponentName)
        )
    }

    /**
     * S27: enable/disable the manifest-declared HomeAlias component at
     * runtime. When disabled, the system stops considering the rental app
     * as a Home candidate, so pressing Home goes to the system launcher
     * (or whatever the user picked before). When enabled, combined with
     * setPersistentHomeActivity above, the rental app captures Home so
     * the customer cannot escape Lock Task Mode by pressing Home.
     */
    private fun setHomeAliasEnabled(enabled: Boolean) {
        val component = ComponentName(activity, HomeAliasComponentName)
        val target = if (enabled) {
            PackageManager.COMPONENT_ENABLED_STATE_ENABLED
        } else {
            PackageManager.COMPONENT_ENABLED_STATE_DISABLED
        }
        try {
            activity.packageManager.setComponentEnabledSetting(
                component,
                target,
                PackageManager.DONT_KILL_APP
            )
        } catch (t: RuntimeException) {
            Log.w(Tag, "setHomeAliasEnabled($enabled) failed", t)
        }
    }

    private fun isLockTaskActive(): Boolean =
        activityManager.lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE

    private fun isPolicyOwner(): Boolean =
        policy.isDeviceOwnerApp(ownPackage) || policy.isProfileOwnerApp(ownPackage)

    private fun grantNotificationPermission(notes: MutableList<String>) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            return
        }

        applyPolicy(notes, "Grant notification permission") {
            val granted = policy.setPermissionGrantState(
                admin,
                ownPackage,
                Manifest.permission.POST_NOTIFICATIONS,
                DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED
            )
            if (!granted) {
                notes += "Notification permission was not accepted by this device."
            }
        }
    }

    private fun applyPolicy(notes: MutableList<String>, label: String, action: () -> Unit) {
        try {
            action()
        } catch (error: RuntimeException) {
            notes += "$label failed: ${error.javaClass.simpleName}"
            Log.e(Tag, "$label failed", error)
        }
    }

    companion object {
        private const val Tag = "RentalKiosk"
        // Fully-qualified name of the activity-alias declared in
        // AndroidManifest.xml. Toggled on/off at runtime by
        // setHomeAliasEnabled to gate whether the rental app is a HOME
        // intent target.
        private const val HomeAliasComponentName =
            "com.rentalsystem.android.HomeAlias"
    }
}
