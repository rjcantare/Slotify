package com.rentalsystem.android

import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.os.Build
import android.util.Log
import com.rentalsystem.android.fresh.EphemeralUserExperimentResult
import com.rentalsystem.android.fresh.EphemeralUserExperimentService
import com.rentalsystem.android.fresh.FreshUserPackageList

class KioskDevReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (!context.isDebuggable()) {
            return
        }

        val policy = context.getSystemService(DevicePolicyManager::class.java)
        val admin = ComponentName(context, RentalDeviceAdminReceiver::class.java)

        when (intent.action) {
            ACTION_EXIT_KIOSK -> exitKiosk(context, policy, admin)
            ACTION_ENTER_KIOSK -> enterKiosk(context)
            ACTION_FRESH_USER_STATUS -> runFreshUserExperiment(context, "status") {
                it.status()
            }
            ACTION_FRESH_USER_CREATE -> runFreshUserExperiment(context, "create") {
                it.createProbeUser()
            }
            ACTION_FRESH_USER_REMOVE -> runFreshUserExperiment(context, "remove") {
                it.removeProbeUser()
            }
            ACTION_FRESH_USER_SWITCH -> runFreshUserExperiment(context, "switch") {
                it.switchToProbeUser()
            }
            ACTION_FRESH_USER_INSTALL_EXISTING -> runFreshUserExperiment(context, "install-existing") {
                it.installExistingPackages(FreshUserPackageList.parse(intent.getStringExtra(EXTRA_PACKAGES)))
            }
        }
    }

    private fun exitKiosk(
        context: Context,
        policy: DevicePolicyManager,
        admin: ComponentName
    ) {
        if (!policy.isDeviceOwnerApp(context.packageName)) {
            return
        }

        applyPolicy("Enable status shade") {
            policy.setStatusBarDisabled(admin, false)
        }
        applyPolicy("Reset lock task system UI") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
            }
        }

        applyPolicy("Remove lock task allowlist") {
            policy.setLockTaskPackages(admin, emptyArray())
        }
        applyPolicy("Clear kiosk Home") {
            policy.clearPackagePersistentPreferredActivities(admin, context.packageName)
        }
        @Suppress("DEPRECATION")
        applyPolicy("Clear Device Owner") {
            policy.clearDeviceOwnerApp(context.packageName)
        }
    }

    private fun enterKiosk(context: Context) {
        val launch = context.packageManager.getLaunchIntentForPackage(context.packageName)
            ?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            ?: return

        context.startActivity(launch)
    }

    private fun runFreshUserExperiment(
        context: Context,
        label: String,
        action: (EphemeralUserExperimentService) -> EphemeralUserExperimentResult
    ) {
        val result = try {
            action(EphemeralUserExperimentService(context))
        } catch (error: RuntimeException) {
            Log.w(Tag, "Fresh-user experiment $label threw", error)
            EphemeralUserExperimentResult(
                ok = false,
                message = "${error.javaClass.simpleName}: ${error.message}"
            )
        }

        val level = if (result.ok) Log.INFO else Log.WARN
        Log.println(level, Tag, "Fresh-user experiment $label: ${result.message}")
    }

    private fun Context.isDebuggable(): Boolean =
        (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0

    private fun applyPolicy(label: String, action: () -> Unit) {
        try {
            action()
        } catch (error: RuntimeException) {
            Log.e(Tag, "$label failed", error)
        }
    }

    companion object {
        private const val Tag = "RentalKiosk"
        const val ACTION_EXIT_KIOSK = "com.rentalsystem.android.action.EXIT_KIOSK"
        const val ACTION_ENTER_KIOSK = "com.rentalsystem.android.action.ENTER_KIOSK"
        const val ACTION_FRESH_USER_STATUS = "com.rentalsystem.android.action.FRESH_USER_EXPERIMENT_STATUS"
        const val ACTION_FRESH_USER_CREATE = "com.rentalsystem.android.action.FRESH_USER_EXPERIMENT_CREATE"
        const val ACTION_FRESH_USER_REMOVE = "com.rentalsystem.android.action.FRESH_USER_EXPERIMENT_REMOVE"
        const val ACTION_FRESH_USER_SWITCH = "com.rentalsystem.android.action.FRESH_USER_EXPERIMENT_SWITCH"
        const val ACTION_FRESH_USER_INSTALL_EXISTING =
            "com.rentalsystem.android.action.FRESH_USER_EXPERIMENT_INSTALL_EXISTING"
        const val EXTRA_PACKAGES = "packages"
    }
}
