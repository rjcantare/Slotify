package com.rentalsystem.android

import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.util.Log

/**
 * Self-launches [MainActivity] when a user finishes booting — but ONLY in a
 * customer user (where this app is Profile Owner, not Device Owner).
 *
 * Why this exists: in customer-user mode the owner creates a fresh ephemeral
 * user and switches into it, but nothing launches the rental app inside that
 * user. A fresh user does not auto-launch a non-default-Home app, so the app
 * never runs its kiosk setup (enable HomeAlias + pin Home via KioskController),
 * and Android shows the Home-app resolver chooser. Bench-proven on 2509ARPBDG
 * 2026-06-01: switching into a fresh customer user lands on
 * com.android.intentresolver.ResolverActivity. The manual experiment avoided
 * this by running `am start --user <id> -n .../MainActivity` by hand
 * (fresh-user-experiment.md, Phase 2). This receiver automates that step.
 *
 * Gated to Profile-Owner-only so it is a no-op in the owner user (which already
 * has the rental app pinned as Home) and whenever customer-user mode is off
 * (no customer user is ever created, so it never fires in one).
 *
 * Manifest-registered (not runtime) so it can start the app's process in a user
 * where the app is not yet running. The app is Profile Owner of the customer
 * user, so it is not in the broadcast-excluded "stopped" state.
 */
class UserBootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        val policy = context.getSystemService(DevicePolicyManager::class.java) ?: return
        val pkg = context.packageName
        val isCustomerUser = policy.isProfileOwnerApp(pkg) && !policy.isDeviceOwnerApp(pkg)
        if (!isCustomerUser) {
            Log.d(Tag, "BOOT_COMPLETED in non-customer user — ignoring.")
            return
        }

        try {
            pinRentalHome(context, policy)
            val launch = (context.packageManager.getLaunchIntentForPackage(pkg)
                ?: Intent(context, MainActivity::class.java))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            context.startActivity(launch)
            Log.i(Tag, "Customer-user BOOT_COMPLETED -> launched MainActivity to claim Home.")
        } catch (t: Throwable) {
            Log.w(Tag, "launch on customer-user boot failed", t)
        }
    }

    private fun pinRentalHome(context: Context, policy: DevicePolicyManager) {
        val admin = ComponentName(context, RentalDeviceAdminReceiver::class.java)
        val homeAlias = ComponentName(context.packageName, "${context.packageName}.HomeAlias")

        context.packageManager.setComponentEnabledSetting(
            homeAlias,
            PackageManager.COMPONENT_ENABLED_STATE_ENABLED,
            PackageManager.DONT_KILL_APP
        )

        val filter = IntentFilter(Intent.ACTION_MAIN).apply {
            addCategory(Intent.CATEGORY_HOME)
            addCategory(Intent.CATEGORY_DEFAULT)
        }
        policy.addPersistentPreferredActivity(admin, filter, homeAlias)
        Log.i(Tag, "Customer-user BOOT_COMPLETED -> pinned HomeAlias before launch.")
    }

    private companion object {
        const val Tag = "UserBootReceiver"
    }
}
