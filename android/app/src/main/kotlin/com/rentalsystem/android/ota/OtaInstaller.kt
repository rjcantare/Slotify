package com.rentalsystem.android.ota

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageInstaller
import android.os.Build
import java.io.File

/**
 * Installs an OTA APK through the system [PackageInstaller] session API.
 *
 * When this app is the Device Owner, the system completes the install with no
 * user-facing confirmation dialog and without routing through the File Manager
 * / MediaStore sideload path that HyperOS rejects with "package appears to be
 * invalid". This is the correct programmatic install path for a Device Owner
 * DPC and sidesteps the HyperOS sideload hostility entirely.
 *
 * Self-update caveat: committing an install of our own package tears down this
 * process the moment the swap succeeds, so [PackageInstaller.STATUS_SUCCESS]
 * usually never reaches [ResultListener] — the new version just relaunches
 * (we are the Home/kiosk app), and that process death IS the success signal.
 * The failure and PENDING_USER_ACTION statuses do arrive before the swap, so
 * the non-Device-Owner confirmation path and error reporting still work.
 */
class OtaInstaller(private val context: Context) {

    fun interface ResultListener {
        /**
         * @param status one of the [PackageInstaller] STATUS_* constants.
         * @param message the system status message, if any.
         * @param userActionIntent present only when [status] is
         *   [PackageInstaller.STATUS_PENDING_USER_ACTION]; launch it to show
         *   the system confirmation dialog (non-Device-Owner installs).
         */
        fun onResult(status: Int, message: String?, userActionIntent: Intent?)
    }

    /**
     * Writes [apk] into a fresh install session and commits it. The receiver
     * for the commit result is registered before commit and unregisters itself
     * on first delivery. Throws if the session cannot be created/written —
     * callers should catch and fall back to the manual sideload flow.
     */
    fun install(apk: File, listener: ResultListener) {
        val installer = context.packageManager.packageInstaller
        val action = "${context.packageName}.OTA_INSTALL_STATUS"

        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                try {
                    ctx.unregisterReceiver(this)
                } catch (_: Throwable) {
                    // Already torn down; ignore.
                }
                val status = intent.getIntExtra(
                    PackageInstaller.EXTRA_STATUS,
                    PackageInstaller.STATUS_FAILURE
                )
                val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                listener.onResult(status, msg, extractUserActionIntent(intent))
            }
        }
        val filter = IntentFilter(action)
        // Android 14+ requires an explicit export flag on dynamically registered
        // receivers; ours is internal-only.
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }

        val params = PackageInstaller.SessionParams(
            PackageInstaller.SessionParams.MODE_FULL_INSTALL
        ).apply {
            setAppPackageName(context.packageName)
        }

        val sessionId = installer.createSession(params)
        installer.openSession(sessionId).use { session ->
            apk.inputStream().use { input ->
                session.openWrite("ota.apk", 0, apk.length()).use { output ->
                    input.copyTo(output)
                    session.fsync(output)
                }
            }
            val statusIntent = Intent(action).setPackage(context.packageName)
            // PackageInstaller fills in result extras, so the PendingIntent must
            // be mutable on API 31+.
            val piFlags = PendingIntent.FLAG_UPDATE_CURRENT or
                (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            val pending = PendingIntent.getBroadcast(context, sessionId, statusIntent, piFlags)
            session.commit(pending.intentSender)
        }
    }

    private fun extractUserActionIntent(intent: Intent): Intent? =
        if (Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(Intent.EXTRA_INTENT)
        }
}
