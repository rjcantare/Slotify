package com.rentalsystem.android.fresh

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.PersistableBundle
import android.os.Process
import android.os.UserHandle
import android.os.UserManager
import android.util.Log
import com.rentalsystem.android.RentalDeviceAdminReceiver
import com.rentalsystem.android.config.FreshUserResetConfig
import com.rentalsystem.android.mqtt.TabletAppConfigMessage
import com.rentalsystem.android.sessions.SessionTrackerState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

class FreshCustomerUserSessionService(
    context: Context,
    private val config: FreshUserResetConfig
) {
    private val appContext = context.applicationContext
    private val policy = appContext.getSystemService(DevicePolicyManager::class.java)
    private val userManager = appContext.getSystemService(UserManager::class.java)
    private val admin = ComponentName(appContext, RentalDeviceAdminReceiver::class.java)
    private val prefs = appContext.getSharedPreferences(PrefsName, Context.MODE_PRIVATE)
    private val mutex = Mutex()

    @Volatile
    private var tabletAppConfig: TabletAppConfigMessage? = null

    @Volatile
    private var lastProvisionKey: String = ""

    val isEnabled: Boolean
        get() = config.customerUserModeEnabled && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P

    val isProfileOwnerCustomerUser: Boolean
        get() = isEnabled && policy.isProfileOwnerApp(appContext.packageName)

    fun updateTabletAppConfig(message: TabletAppConfigMessage) {
        tabletAppConfig = message
    }

    suspend fun handleObservedSessionState(
        state: SessionTrackerState,
        beforeOwnerSwitch: suspend () -> Unit
    ): FreshCustomerUserResult =
        withContext(Dispatchers.IO) {
            mutex.withLock {
                if (!isEnabled) {
                    return@withLock FreshCustomerUserResult.Disabled
                }

                if (policy.isDeviceOwnerApp(appContext.packageName)) {
                    return@withLock handleOwnerState(state, beforeOwnerSwitch)
                }

                if (policy.isProfileOwnerApp(appContext.packageName)) {
                    return@withLock handleCustomerState(state)
                }

                FreshCustomerUserResult.Failed("App is not Device Owner or Profile Owner.")
            }
        }

    suspend fun handleStaffCustomerLogout(): FreshCustomerUserResult =
        withContext(Dispatchers.IO) {
            mutex.withLock {
                if (!isEnabled) {
                    return@withLock FreshCustomerUserResult.Disabled
                }

                if (policy.isDeviceOwnerApp(appContext.packageName)) {
                    return@withLock removeStoredCustomerUserIfPresent()
                }

                if (policy.isProfileOwnerApp(appContext.packageName)) {
                    return@withLock wipeCurrentCustomerUser()
                }

                FreshCustomerUserResult.Failed("App is not Device Owner or Profile Owner.")
            }
        }

    suspend fun removeStoredCustomerUserAfterOwnerReturn(): FreshCustomerUserResult =
        withContext(Dispatchers.IO) {
            mutex.withLock {
                if (!isEnabled) {
                    return@withLock FreshCustomerUserResult.Disabled
                }

                if (!policy.isDeviceOwnerApp(appContext.packageName)) {
                    return@withLock FreshCustomerUserResult.Noop("Current user is not Device Owner.")
                }

                removeStoredCustomerUserIfPresent()
            }
        }

    private suspend fun handleOwnerState(
        state: SessionTrackerState,
        beforeOwnerSwitch: suspend () -> Unit
    ): FreshCustomerUserResult {
        val active = state.hasSession && !state.locked && state.effectiveRemainingSec > 0
        if (!active) {
            return removeStoredCustomerUserIfPresent()
        }

        if (state.sessionId.isBlank()) {
            return FreshCustomerUserResult.Noop("Active session has no sessionId.")
        }

        ensureAffiliationIds()

        val storedSessionId = prefs.getString(KeySessionId, null)
        val storedUser = storedCustomerUser()
        val customerUser = if (storedSessionId == state.sessionId && storedUser != null) {
            storedUser
        } else {
            if (storedUser != null) {
                removeUser(storedUser)
            }
            createCustomerUser(state.sessionId)
        }

        beforeOwnerSwitch()
        prewarmCustomerUser(customerUser)
        val switched = policy.switchUser(admin, customerUser)
        return if (switched) {
            FreshCustomerUserResult.SwitchedToCustomerUser(state.sessionId)
        } else {
            FreshCustomerUserResult.Failed("switchUser returned false.")
        }
    }

    private fun handleCustomerState(state: SessionTrackerState): FreshCustomerUserResult {
        ensureAffiliationIds()
        val active = state.hasSession && !state.locked && state.effectiveRemainingSec > 0
        if (!active) {
            return wipeCurrentCustomerUser()
        }

        val packages = enabledAllowedPackages()
        if (packages.isEmpty()) {
            return FreshCustomerUserResult.Noop("No server-allowed packages to provision.")
        }

        val provisionKey = "${state.sessionId}|" + packages.joinToString("|")
        if (provisionKey == lastProvisionKey) {
            return FreshCustomerUserResult.Noop("Allowed packages already provisioned for this session.")
        }

        val installed = mutableListOf<String>()
        val failed = mutableListOf<String>()
        packages.forEach { packageName ->
            try {
                if (policy.installExistingPackage(admin, packageName)) {
                    installed += packageName
                } else {
                    failed += "$packageName:false"
                }
            } catch (error: RuntimeException) {
                Log.w(Tag, "installExistingPackage failed for $packageName", error)
                failed += "$packageName:${error.javaClass.simpleName}"
            }
        }

        lastProvisionKey = provisionKey
        return FreshCustomerUserResult.PackagesProvisioned(installed, failed)
    }

    private fun createCustomerUser(sessionId: String): UserHandle {
        val flags = DevicePolicyManager.SKIP_SETUP_WIZARD or
            DevicePolicyManager.MAKE_USER_EPHEMERAL or
            DevicePolicyManager.LEAVE_ALL_SYSTEM_APPS_ENABLED

        val user = policy.createAndManageUser(
            admin,
            CustomerUserName,
            admin,
            PersistableBundle(),
            flags
        ) ?: throw IllegalStateException("createAndManageUser returned null.")

        val serial = userManager.getSerialNumberForUser(user)
        prefs.edit()
            .putLong(KeyUserSerial, serial)
            .putString(KeySessionId, sessionId)
            .apply()

        return user
    }

    private suspend fun prewarmCustomerUser(user: UserHandle) {
        val result = try {
            policy.startUserInBackground(admin, user)
        } catch (error: RuntimeException) {
            Log.w(Tag, "customer user: background start failed", error)
            return
        }

        if (result != UserManager.USER_OPERATION_SUCCESS) {
            Log.w(Tag, "customer user: background start returned $result")
            return
        }

        // Give BOOT_COMPLETED time to reach the Profile Owner app so it can
        // launch MainActivity and pin HomeAlias before foreground switch.
        Log.i(Tag, "customer user: background prewarm started")
        delay(CustomerUserPrewarmDelayMs)
    }

    /**
     * Every package (other than ours) that handles the HOME intent — i.e. every
     * launcher that could compete with the rental app's HomeAlias and make
     * Android show the "choose a Home app" resolver in the fresh customer user.
     *
     * Discovered dynamically instead of hardcoded. The previous hardcoded list
     * led with "com.miui.home", but this POCO / HyperOS global build ships the
     * launcher as "com.mi.android.globallauncher" (verified on 2509ARPBDG via
     * `pm query-activities -a MAIN -c HOME`), so the real launcher was never
     * hidden and the resolver appeared — the exact symptom that shelved
     * customer-user mode. Querying covers any OEM build.
     *
     * Settings (FallbackHome) and the setup wizard also answer HOME but are
     * excluded: they must never be hidden, and their low priority means they
     * don't compete with a pinned HomeAlias.
     */
    @Suppress("DEPRECATION")
    private fun competingHomePackages(includeHidden: Boolean = false): Set<String> {
        val home = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
        // MATCH_UNINSTALLED_PACKAGES is needed to still see a launcher that is
        // currently hidden (setApplicationHidden treats the app as uninstalled
        // for that user) — required by restoreOwnerLaunchers below.
        val flags = if (includeHidden) {
            PackageManager.MATCH_UNINSTALLED_PACKAGES
        } else {
            PackageManager.MATCH_DEFAULT_ONLY
        }
        return appContext.packageManager
            .queryIntentActivities(home, flags)
            .asSequence()
            .mapNotNull { it.activityInfo?.packageName }
            .filter { it != appContext.packageName }
            .filterNot { it in ProtectedHomePackages }
            .toSet()
    }

    /**
     * Defensive self-heal for interrupted customer-user cycles. Because
     * setApplicationHidden is OWNER-user-scoped (verified on 2509ARPBDG:
     * hiding from the owner sets hidden=true in user 0, not the customer user),
     * a cycle that hid the system launcher but never reached its re-enable path
     * (crash, manual user removal, power loss) leaves the OWNER launcher hidden.
     * Shell cannot un-hide it (needs MANAGE_USERS), so only the Device Owner can.
     * Run on app startup; safe no-op when nothing is hidden.
     */
    fun restoreOwnerLaunchers() {
        if (!policy.isDeviceOwnerApp(appContext.packageName)) return
        for (pkg in ownerLauncherRestorePackages()) {
            try {
                policy.setApplicationHidden(admin, pkg, false)
                Log.i(Tag, "owner launcher restored: $pkg")
            } catch (_: Exception) { }
        }
    }

    private fun removeStoredCustomerUserIfPresent(): FreshCustomerUserResult {
        val user = storedCustomerUser()
        if (user == null) {
            prefs.edit().remove(KeyUserSerial).remove(KeySessionId).apply()
            return FreshCustomerUserResult.Noop("No stored customer user.")
        }

        return if (removeUser(user)) {
            prefs.edit().remove(KeyUserSerial).remove(KeySessionId).apply()
            FreshCustomerUserResult.RemovedCustomerUser
        } else {
            FreshCustomerUserResult.Failed("removeUser returned false.")
        }
    }

    private fun removeUser(user: UserHandle): Boolean =
        try {
            restoreOwnerLaunchers()
            policy.removeUser(admin, user)
        } catch (error: RuntimeException) {
            Log.w(Tag, "remove customer user failed", error)
            false
        }

    private fun logoutCustomerUser(): FreshCustomerUserResult {
        return try {
            val result = policy.logoutUser(admin)
            FreshCustomerUserResult.LogoutRequested(result)
        } catch (error: RuntimeException) {
            Log.w(Tag, "logout customer user failed", error)
            FreshCustomerUserResult.Failed("logoutUser failed: ${error.javaClass.simpleName}: ${error.message}")
        }
    }

    private fun wipeCurrentCustomerUser(): FreshCustomerUserResult {
        return try {
            // Staff-forced logout must delete the whole ephemeral customer user,
            // not just switch back to Owner. Otherwise game account state
            // survives and reappears on the next paid session.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                policy.wipeData(
                    DevicePolicyManager.WIPE_SILENTLY,
                    "Rental staff customer logout"
                )
            } else {
                @Suppress("DEPRECATION")
                policy.wipeData(0)
            }
            FreshCustomerUserResult.CustomerUserWipeRequested
        } catch (error: RuntimeException) {
            Log.w(Tag, "wipe customer user failed", error)
            FreshCustomerUserResult.Failed("wipeData failed: ${error.javaClass.simpleName}: ${error.message}")
        }
    }

    private fun storedCustomerUser(): UserHandle? {
        val serial = prefs.getLong(KeyUserSerial, NoSerial)
        return serial.takeIf { it != NoSerial }?.let { userManager.getUserForSerialNumber(it) }
    }

    private fun ensureAffiliationIds() {
        policy.setAffiliationIds(admin, AffiliationIds)
    }

    private fun enabledAllowedPackages(): List<String> =
        tabletAppConfig
            ?.apps
            ?.asSequence()
            ?.filter { it.enabled }
            ?.map { it.packageName.trim() }
            ?.filter { it.isNotBlank() && it != appContext.packageName }
            ?.distinct()
            ?.toList()
            ?: emptyList()

    fun currentUserSerial(): Long =
        userManager.getSerialNumberForUser(Process.myUserHandle())

    private fun ownerLauncherRestorePackages(): Set<String> =
        competingHomePackages(includeHidden = true) + KnownLauncherPackages

    companion object {
        private const val Tag = "FreshCustomerUser"
        private const val PrefsName = "fresh_customer_user"
        private const val KeyUserSerial = "customer_user_serial"
        private const val KeySessionId = "customer_session_id"
        private const val NoSerial = -1L
        private const val CustomerUserPrewarmDelayMs = 8_000L
        private const val CustomerUserName = "Rental Customer"
        private val AffiliationIds = setOf("rental-system")

        // HOME-intent handlers that must never be hidden when isolating the
        // customer user's launcher. Settings provides FallbackHome; the setup
        // wizard owns HOME transiently on a fresh user.
        private val ProtectedHomePackages = setOf(
            "com.android.settings",
            "com.google.android.setupwizard",
            "android"
        )

        // Fallback restore list for launchers hidden by earlier bench builds.
        // Hidden packages can disappear from HOME queries on OEM builds.
        private val KnownLauncherPackages = setOf(
            "com.mi.android.globallauncher",
            "com.miui.home",
            "com.google.android.apps.nexuslauncher",
            "com.android.launcher3",
            "com.android.launcher",
            "com.sec.android.app.launcher",
            "com.oppo.launcher",
            "com.huawei.android.launcher"
        )
    }
}

sealed class FreshCustomerUserResult {
    data object Disabled : FreshCustomerUserResult()
    data class Noop(val reason: String) : FreshCustomerUserResult()
    data class SwitchedToCustomerUser(val sessionId: String) : FreshCustomerUserResult()
    data class PackagesProvisioned(
        val installed: List<String>,
        val failed: List<String>
    ) : FreshCustomerUserResult()
    data object RemovedCustomerUser : FreshCustomerUserResult()
    data object CustomerUserWipeRequested : FreshCustomerUserResult()
    data class LogoutRequested(val resultCode: Int) : FreshCustomerUserResult()
    data class Failed(val reason: String) : FreshCustomerUserResult()
}
