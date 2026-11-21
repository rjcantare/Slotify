package com.rentalsystem.android.fresh

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.PersistableBundle
import android.os.Process
import android.os.UserHandle
import android.os.UserManager
import android.util.Log
import com.rentalsystem.android.RentalDeviceAdminReceiver

/**
 * Bench-only experiment for the "real deep freeze" idea: create a temporary
 * Android user instead of wiping big game app data. This is intentionally not
 * wired into session-end cleanup yet. Xiaomi/HyperOS must prove it allows the
 * Device Owner user-management APIs before this can become a product feature.
 */
class EphemeralUserExperimentService(context: Context) {
    private val appContext = context.applicationContext
    private val policy = appContext.getSystemService(DevicePolicyManager::class.java)
    private val userManager = appContext.getSystemService(UserManager::class.java)
    private val admin = ComponentName(appContext, RentalDeviceAdminReceiver::class.java)
    private val prefs = appContext.getSharedPreferences(PrefsName, Context.MODE_PRIVATE)

    fun status(): EphemeralUserExperimentResult {
        val storedSerial = prefs.getLong(KeyUserSerial, NoSerial)
        val existingUser = storedSerial.takeIf { it != NoSerial }?.let { serial ->
            userManager.getUserForSerialNumber(serial)
        }
        val currentUserSerial = userManager.getSerialNumberForUser(Process.myUserHandle())
        val affiliated = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            policy.isAffiliatedUser()
        } else {
            false
        }

        return EphemeralUserExperimentResult(
            ok = isDeviceOrProfileOwner(),
            message = "deviceOwner=${policy.isDeviceOwnerApp(appContext.packageName)}, " +
                "profileOwner=${policy.isProfileOwnerApp(appContext.packageName)}, " +
                "affiliated=$affiliated, currentUserSerial=$currentUserSerial, sdk=${Build.VERSION.SDK_INT}, " +
                "multipleUsers=${UserManager.supportsMultipleUsers()}, " +
                "storedSerial=${storedSerial.takeIf { it != NoSerial } ?: "none"}, " +
                "storedUserExists=${existingUser != null}"
        )
    }

    fun createProbeUser(): EphemeralUserExperimentResult {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
            return EphemeralUserExperimentResult(false, "Android 9/P+ required for this experiment.")
        }

        if (!policy.isDeviceOwnerApp(appContext.packageName)) {
            return EphemeralUserExperimentResult(false, "App is not Device Owner.")
        }

        val storedSerial = prefs.getLong(KeyUserSerial, NoSerial)
        if (storedSerial != NoSerial && userManager.getUserForSerialNumber(storedSerial) != null) {
            return EphemeralUserExperimentResult(true, "Probe user already exists: serial=$storedSerial")
        }

        return try {
            ensureAffiliationIds()

            val flags = DevicePolicyManager.SKIP_SETUP_WIZARD or
                DevicePolicyManager.MAKE_USER_EPHEMERAL or
                DevicePolicyManager.LEAVE_ALL_SYSTEM_APPS_ENABLED

            val user = policy.createAndManageUser(
                admin,
                ProbeUserName,
                admin,
                PersistableBundle(),
                flags
            )

            if (user == null) {
                EphemeralUserExperimentResult(false, "createAndManageUser returned null.")
            } else {
                val serial = userManager.getSerialNumberForUser(user)
                prefs.edit().putLong(KeyUserSerial, serial).apply()
                EphemeralUserExperimentResult(true, "Created probe user: serial=$serial")
            }
        } catch (error: RuntimeException) {
            Log.w(Tag, "create probe user failed", error)
            EphemeralUserExperimentResult(false, "Create failed: ${error.javaClass.simpleName}: ${error.message}")
        }
    }

    fun removeProbeUser(): EphemeralUserExperimentResult {
        if (!policy.isDeviceOwnerApp(appContext.packageName)) {
            return EphemeralUserExperimentResult(false, "App is not Device Owner.")
        }

        val storedSerial = prefs.getLong(KeyUserSerial, NoSerial)
        if (storedSerial == NoSerial) {
            return EphemeralUserExperimentResult(true, "No stored probe user serial.")
        }

        val user = userManager.getUserForSerialNumber(storedSerial)
        if (user == null) {
            prefs.edit().remove(KeyUserSerial).apply()
            return EphemeralUserExperimentResult(true, "Stored probe user no longer exists; cleared serial.")
        }

        return try {
            val removed = policy.removeUser(admin, user)
            if (removed) {
                prefs.edit().remove(KeyUserSerial).apply()
                EphemeralUserExperimentResult(true, "Removed probe user: serial=$storedSerial")
            } else {
                EphemeralUserExperimentResult(false, "removeUser returned false for serial=$storedSerial")
            }
        } catch (error: RuntimeException) {
            Log.w(Tag, "remove probe user failed", error)
            EphemeralUserExperimentResult(false, "Remove failed: ${error.javaClass.simpleName}: ${error.message}")
        }
    }

    fun switchToProbeUser(): EphemeralUserExperimentResult {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
            return EphemeralUserExperimentResult(false, "Android 9/P+ required for this experiment.")
        }

        if (!policy.isDeviceOwnerApp(appContext.packageName)) {
            return EphemeralUserExperimentResult(false, "App is not Device Owner.")
        }

        val storedSerial = prefs.getLong(KeyUserSerial, NoSerial)
        if (storedSerial == NoSerial) {
            return EphemeralUserExperimentResult(false, "No stored probe user serial.")
        }

        val user = userManager.getUserForSerialNumber(storedSerial)
        if (user == null) {
            prefs.edit().remove(KeyUserSerial).apply()
            return EphemeralUserExperimentResult(false, "Stored probe user no longer exists; cleared serial.")
        }

        return try {
            val switched = policy.switchUser(admin, user)
            if (switched) {
                EphemeralUserExperimentResult(true, "Switch requested to probe user: serial=$storedSerial")
            } else {
                EphemeralUserExperimentResult(false, "switchUser returned false for serial=$storedSerial")
            }
        } catch (error: RuntimeException) {
            Log.w(Tag, "switch to probe user failed", error)
            EphemeralUserExperimentResult(false, "Switch failed: ${error.javaClass.simpleName}: ${error.message}")
        }
    }

    fun installExistingPackages(packageNames: List<String>): EphemeralUserExperimentResult {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
            return EphemeralUserExperimentResult(false, "Android 9/P+ required for installExistingPackage.")
        }

        if (!isDeviceOrProfileOwner()) {
            return EphemeralUserExperimentResult(false, "App is not Device Owner or Profile Owner.")
        }

        val affiliation = ensureAffiliationIds()
        if (!affiliation.ok) {
            return affiliation
        }

        val requested = packageNames
            .asSequence()
            .map { it.trim() }
            .filter { it.isNotBlank() }
            .distinct()
            .toList()

        if (requested.isEmpty()) {
            return EphemeralUserExperimentResult(false, "No package names supplied.")
        }

        val installed = mutableListOf<String>()
        val failed = mutableListOf<String>()
        requested.forEach { packageName ->
            try {
                if (policy.installExistingPackage(admin, packageName)) {
                    installed += packageName
                } else {
                    failed += "$packageName:false"
                }
            } catch (error: RuntimeException) {
                Log.w(Tag, "install-existing failed for $packageName", error)
                failed += "$packageName:${error.javaClass.simpleName}"
            }
        }

        val message = "install-existing requested=${requested.size}, " +
            "installed=${installed.joinToString(prefix = "[", postfix = "]")}, " +
            "failed=${failed.joinToString(prefix = "[", postfix = "]")}"
        return EphemeralUserExperimentResult(ok = failed.isEmpty(), message = message)
    }

    fun storedProbeUser(): UserHandle? {
        val storedSerial = prefs.getLong(KeyUserSerial, NoSerial)
        return storedSerial.takeIf { it != NoSerial }?.let { userManager.getUserForSerialNumber(it) }
    }

    private fun isDeviceOrProfileOwner(): Boolean =
        policy.isDeviceOwnerApp(appContext.packageName) ||
            policy.isProfileOwnerApp(appContext.packageName)

    private fun ensureAffiliationIds(): EphemeralUserExperimentResult {
        if (!isDeviceOrProfileOwner()) {
            return EphemeralUserExperimentResult(false, "App is not Device Owner or Profile Owner.")
        }

        return try {
            policy.setAffiliationIds(admin, AffiliationIds)
            val affiliated = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                policy.isAffiliatedUser()
            } else {
                true
            }
            EphemeralUserExperimentResult(true, "Affiliation ids set: affiliated=$affiliated")
        } catch (error: RuntimeException) {
            Log.w(Tag, "set affiliation ids failed", error)
            EphemeralUserExperimentResult(
                false,
                "Affiliation failed: ${error.javaClass.simpleName}: ${error.message}"
            )
        }
    }

    companion object {
        private const val Tag = "FreshUserExperiment"
        private const val PrefsName = "fresh_user_experiment"
        private const val KeyUserSerial = "probe_user_serial"
        private const val NoSerial = -1L
        private const val ProbeUserName = "Rental Test Customer"
        private val AffiliationIds = setOf("rental-system")
    }
}

data class EphemeralUserExperimentResult(
    val ok: Boolean,
    val message: String
)
