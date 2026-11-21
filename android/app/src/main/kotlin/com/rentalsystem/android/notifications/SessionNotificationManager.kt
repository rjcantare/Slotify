package com.rentalsystem.android.notifications

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import com.rentalsystem.android.sessions.SessionTrackerState

// S28: persistent ongoing notification shown whenever the customer's session
// is active. Carries a live countdown plus two action buttons (Add Time,
// Clear Time) so the customer can manage their session WITHOUT returning to
// the rental kiosk. When the session goes locked or runs out, the
// notification is dismissed and the rental app re-engages the kiosk via
// the existing S27 path.
class SessionNotificationManager(private val context: Context) {

    fun ensureChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel = NotificationChannel(
            ChannelId,
            "Rental Session",
            // Importance LOW: visible in the shade with no sound or heads-up.
            // The customer should be able to see it but it should not nag.
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Shows your remaining rental time and lets you add more time or clear the session."
            setShowBadge(false)
            setSound(null, null)
            enableVibration(false)
        }
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(channel)

        // Logout-reminder channel: IMPORTANCE_HIGH so the one-shot "log out
        // before your time ends" nudge pops as a heads-up over the game the
        // customer is playing. Separate from the LOW session channel which is
        // deliberately quiet.
        val reminderChannel = NotificationChannel(
            ReminderChannelId,
            "Logout reminder",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "Reminds you to log out of your game accounts before your rental time ends."
            setShowBadge(true)
        }
        nm.createNotificationChannel(reminderChannel)
    }

    /**
     * Posts or updates the persistent notification for the current session.
     * Returns true when the notification was posted; false when the state
     * meant the notification should be dismissed instead.
     */
    fun showOrUpdate(state: SessionTrackerState): Boolean {
        if (!state.hasSession || state.locked || state.effectiveRemainingSec <= 0) {
            dismiss()
            return false
        }
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.notify(NotificationId, buildNotification(state))
        return true
    }

    fun dismiss() {
        context.getSystemService(NotificationManager::class.java).cancel(NotificationId)
    }

    /**
     * One-shot heads-up reminder to log out of game accounts before the
     * session ends. Auto-cancels on tap, not ongoing. Fired from the
     * countdown ticker at the first-warning threshold.
     */
    fun showLogoutReminder(message: String) {
        val nm = context.getSystemService(NotificationManager::class.java)
        @Suppress("DEPRECATION")
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(context, ReminderChannelId)
        } else {
            Notification.Builder(context).setPriority(Notification.PRIORITY_HIGH)
        }
        val notification = builder
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle("Time almost up")
            .setContentText(message)
            .setStyle(Notification.BigTextStyle().bigText(message))
            .setContentIntent(pendingActivity())
            .setAutoCancel(true)
            .setCategory(Notification.CATEGORY_REMINDER)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .build()
        nm.notify(ReminderNotificationId, notification)
    }

    fun buildNotification(state: SessionTrackerState): Notification {
        val addTimeIntent = pendingBroadcast(0, SessionNotificationActionReceiver.ActionAddTime)
        val clearTimeIntent = pendingBroadcast(1, SessionNotificationActionReceiver.ActionClearTime)
        val openAppIntent = pendingActivity()
        val remainingMillis = (state.effectiveRemainingSec * 1000L).coerceAtLeast(1000L)
        val countdownEndsAtWallClockMillis = System.currentTimeMillis() + remainingMillis

        // Use the platform Notification.Builder (no androidx.core dependency).
        @Suppress("DEPRECATION")
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(context, ChannelId)
        } else {
            Notification.Builder(context)
        }

        return builder
            .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
            .setContentTitle("Rental ${state.stationId} active")
            .setContentText("Time remaining")
            .setWhen(countdownEndsAtWallClockMillis)
            .setUsesChronometer(true)
            .setChronometerCountDown(true)
            .setTimeoutAfter(remainingMillis)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(true)
            .setCategory(Notification.CATEGORY_SERVICE)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setContentIntent(openAppIntent)
            .addAction(
                Notification.Action.Builder(
                    null as android.graphics.drawable.Icon?,
                    "Add Time",
                    addTimeIntent
                ).build()
            )
            .addAction(
                Notification.Action.Builder(
                    null as android.graphics.drawable.Icon?,
                    "Clear Time",
                    clearTimeIntent
                ).build()
            )
            .build()
    }

    private fun pendingBroadcast(requestCode: Int, action: String): PendingIntent {
        val intent = Intent(action).setPackage(context.packageName)
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        return PendingIntent.getBroadcast(context, requestCode, intent, flags)
    }

    private fun pendingActivity(): PendingIntent {
        val intent = context.packageManager.getLaunchIntentForPackage(context.packageName)
            ?: Intent()
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        return PendingIntent.getActivity(context, 2, intent, flags)
    }

    companion object {
        private const val ChannelId = "rental_session"
        private const val ReminderChannelId = "rental_logout_reminder"
        const val NotificationId = 1001
        const val ReminderNotificationId = 1002
    }
}
