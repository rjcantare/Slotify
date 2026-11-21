package com.rentalsystem.android.apps

import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.os.Build
import com.rentalsystem.android.mqtt.TabletInstalledAppMessage

object InstalledAppInventory {
    fun collect(context: Context): List<TabletInstalledAppMessage> {
        val packageManager = context.packageManager
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        val resolved = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            packageManager.queryIntentActivities(intent, PackageManager.ResolveInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            packageManager.queryIntentActivities(intent, 0)
        }

        return resolved
            .asSequence()
            .mapNotNull { info ->
                val packageName = info.activityInfo?.packageName ?: return@mapNotNull null
                if (packageName == context.packageName) {
                    return@mapNotNull null
                }

                val label = info.loadLabel(packageManager)?.toString()?.trim().orEmpty()
                if (label.isBlank()) {
                    return@mapNotNull null
                }

                TabletInstalledAppMessage(
                    displayName = label,
                    packageName = packageName,
                    system = isSystemApp(packageManager, packageName)
                )
            }
            .distinctBy { it.packageName }
            .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.displayName })
            .toList()
    }

    private fun isSystemApp(packageManager: PackageManager, packageName: String): Boolean =
        try {
            val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                packageManager.getApplicationInfo(
                    packageName,
                    PackageManager.ApplicationInfoFlags.of(0)
                ).flags
            } else {
                @Suppress("DEPRECATION")
                packageManager.getApplicationInfo(packageName, 0).flags
            }
            (flags and ApplicationInfo.FLAG_SYSTEM) != 0 ||
                (flags and ApplicationInfo.FLAG_UPDATED_SYSTEM_APP) != 0
        } catch (_: PackageManager.NameNotFoundException) {
            false
        }
}
