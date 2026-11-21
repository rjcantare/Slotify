package com.rentalsystem.android.fresh

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.rentalsystem.android.RentalDeviceAdminReceiver
import com.rentalsystem.android.config.FreshUserResetConfig
import com.rentalsystem.android.mqtt.TabletAppConfigMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.util.concurrent.Executor
import kotlin.coroutines.resume

class FreshUserResetService(
    context: Context,
    private val config: FreshUserResetConfig
) {
    private val appContext = context.applicationContext
    private val policy = appContext.getSystemService(DevicePolicyManager::class.java)
    private val admin = ComponentName(appContext, RentalDeviceAdminReceiver::class.java)
    private val mainExecutor = Executor { command ->
        Handler(Looper.getMainLooper()).post(command)
    }

    @Volatile
    private var tabletAppConfig: TabletAppConfigMessage? = null

    fun updateTabletAppConfig(message: TabletAppConfigMessage) {
        tabletAppConfig = message
    }

    suspend fun runAfterSessionEnded(sessionId: String): FreshUserResetSummary =
        withContext(Dispatchers.IO) {
            if (!config.enabled) {
                return@withContext FreshUserResetSummary.Disabled
            }

            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
                Log.w(Tag, "fresh-user cleanup skipped for $sessionId: Android P+ required")
                return@withContext FreshUserResetSummary.UnsupportedSdk
            }

            if (!policy.isDeviceOwnerApp(appContext.packageName)) {
                Log.w(Tag, "fresh-user cleanup skipped for $sessionId: app is not Device Owner")
                return@withContext FreshUserResetSummary.NotDeviceOwner
            }

            val packageNames = deepFreezePackages()
                .distinct()
                .filterNot { shouldSkipPackage(it) }

            if (packageNames.isEmpty()) {
                Log.i(Tag, "fresh-user cleanup has no target packages for $sessionId")
                return@withContext FreshUserResetSummary.Completed(emptyList())
            }

            val results = packageNames.map { packageName ->
                clearPackage(packageName)
            }

            val successCount = results.count { it.succeeded }
            val skippedCount = results.count { it.skippedReason != null }
            Log.i(
                Tag,
                "fresh-user cleanup finished for $sessionId: " +
                    "$successCount cleared, $skippedCount skipped, ${results.size - successCount - skippedCount} failed"
            )
            FreshUserResetSummary.Completed(results)
        }

    /**
     * Staff one-tap reset: clear (log out) an EXPLICIT list of packages chosen
     * by the operator. Unlike runAfterSessionEnded this is NOT gated on
     * config.enabled — it is an on-demand action — but it keeps the SDK,
     * Device-Owner, protected-package and own-package guards. Safe in any
     * session state.
     */
    suspend fun clearPackages(packageNames: List<String>): FreshUserResetSummary =
        withContext(Dispatchers.IO) {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
                Log.w(Tag, "staff app-reset skipped: Android P+ required")
                return@withContext FreshUserResetSummary.UnsupportedSdk
            }

            if (!policy.isDeviceOwnerApp(appContext.packageName)) {
                Log.w(Tag, "staff app-reset skipped: app is not Device Owner")
                return@withContext FreshUserResetSummary.NotDeviceOwner
            }

            val targets = packageNames
                .map { it.trim() }
                .filter { it.isNotBlank() }
                .distinct()
                .filterNot { shouldSkipPackage(it) }

            if (targets.isEmpty()) {
                Log.i(Tag, "staff app-reset has no eligible target packages")
                return@withContext FreshUserResetSummary.Completed(emptyList())
            }

            val results = targets.map { clearPackage(it) }
            val successCount = results.count { it.succeeded }
            Log.i(Tag, "staff app-reset finished: $successCount cleared of ${results.size} requested")
            FreshUserResetSummary.Completed(results)
        }

    private fun shouldSkipPackage(packageName: String): Boolean {
        if (packageName == appContext.packageName) {
            Log.w(Tag, "fresh-user cleanup refused to clear own package: $packageName")
            return true
        }

        if (packageName in ProtectedPackages) {
            Log.w(Tag, "fresh-user cleanup refused to clear protected package: $packageName")
            return true
        }

        return false
    }

    private fun deepFreezePackages(): List<String> {
        val serverPackages = tabletAppConfig
            ?.apps
            ?.asSequence()
            ?.filter { it.enabled && it.deepFreeze }
            ?.map { it.packageName.trim() }
            ?.filter { it.isNotBlank() }
            ?.toSet()
            ?: emptySet()

        // Always merge the local fallback — the server config should ADD to
        // the safety net, not replace it. If the operator forgets to check
        // deepFreeze for a game, the local list still catches it.
        val localPackages = config.clearApplicationDataPackages
            .asSequence()
            .map { it.trim() }
            .filter { it.isNotBlank() }
            .toSet()

        return (serverPackages + localPackages).toList()
    }

    private suspend fun clearPackage(packageName: String): FreshUserResetResult {
        if (!isInstalled(packageName)) {
            Log.d(Tag, "fresh-user cleanup skipped missing package: $packageName")
            return FreshUserResetResult(packageName, succeeded = false, skippedReason = "not_installed")
        }

        return suspendCancellableCoroutine { continuation ->
            try {
                policy.clearApplicationUserData(
                    admin,
                    packageName,
                    mainExecutor,
                    DevicePolicyManager.OnClearApplicationUserDataListener { clearedPackage, succeeded ->
                        if (continuation.isActive) {
                            if (!succeeded) {
                                Log.w(Tag, "fresh-user cleanup failed for $clearedPackage")
                            }
                            continuation.resume(
                                FreshUserResetResult(
                                    packageName = clearedPackage,
                                    succeeded = succeeded,
                                    skippedReason = null
                                )
                            )
                        }
                    }
                )
            } catch (error: RuntimeException) {
                Log.w(Tag, "fresh-user cleanup threw for $packageName", error)
                if (continuation.isActive) {
                    continuation.resume(
                        FreshUserResetResult(packageName, succeeded = false, skippedReason = null))
                }
            }
        }
    }

    private fun isInstalled(packageName: String): Boolean =
        try {
            appContext.packageManager.getPackageInfo(packageName, 0)
            true
        } catch (_: PackageManager.NameNotFoundException) {
            false
        }

    companion object {
        private const val Tag = "FreshUserReset"

        private val ProtectedPackages = setOf(
            "android",
            "com.android.settings",
            "com.android.systemui",
            "com.google.android.gms",
            "com.google.android.gsf"
        )
    }
}

sealed class FreshUserResetSummary {
    data object Disabled : FreshUserResetSummary()
    data object UnsupportedSdk : FreshUserResetSummary()
    data object NotDeviceOwner : FreshUserResetSummary()
    data class Completed(val results: List<FreshUserResetResult>) : FreshUserResetSummary()
}

data class FreshUserResetResult(
    val packageName: String,
    val succeeded: Boolean,
    val skippedReason: String?
)
