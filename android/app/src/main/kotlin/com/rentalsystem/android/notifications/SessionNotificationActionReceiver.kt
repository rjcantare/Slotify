package com.rentalsystem.android.notifications

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import com.rentalsystem.android.RentalAndroidApplication
import com.rentalsystem.android.claims.ClaimStatus
import com.rentalsystem.android.mqtt.MqttConnectionStatus
import kotlinx.coroutines.launch

// S28: handles taps on the persistent notification's Add Time / Clear Time
// action buttons. Mirrors the same publish paths that MainActivity uses for
// the in-app buttons, but driven from the application context so it works
// when the rental Activity is backgrounded.
//
// Offline handling (reported 2026-05-24 "cant add and clear time mqtt offline"):
//   - If the HiveMQ connection is down at tap time, the action is queued in
//     the Application's pendingNotificationAction slot. The Application's
//     MQTT status listener fires the queued action on the next ONLINE
//     transition, so the customer's tap is not silently lost during a WiFi
//     blip.
//   - A Toast is shown so the customer knows their tap landed even though
//     the action will only execute moments later.
//   - To shorten the wait, we ALSO call mqttClient.connect() to force HiveMQ
//     out of its exponential-backoff sleep and try reconnecting now.
class SessionNotificationActionReceiver : BroadcastReceiver() {

    private val tag = "RentalNotifAction"
    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onReceive(context: Context, intent: Intent) {
        val app = context.applicationContext as? RentalAndroidApplication ?: return
        if (app.configError != null) return

        when (intent.action) {
            ActionAddTime -> handleAddTime(app, context)
            ActionClearTime -> handleClearTime(app, context)
        }
    }

    private fun handleAddTime(app: RentalAndroidApplication, context: Context) {
        val current = app.claimTracker.getCurrentState()
        if (current.status == ClaimStatus.Pending || current.status == ClaimStatus.Granted) {
            toast(context, "Coin window already open. Drop coins now.")
            return
        }
        val client = app.mqttClientOrNull()
        if (client == null || !client.isConnected) {
            toast(context, "Reconnecting to server… Add Time will fire when online.")
            app.queuePendingNotificationAction(PendingNotificationAction.AddTime)
            app.kickReconnect()
            return
        }
        publishAddTime(app, client)
    }

    private fun handleClearTime(app: RentalAndroidApplication, context: Context) {
        val session = app.sessionTracker.getCurrentState()
        if (!session.hasSession || session.locked || session.effectiveRemainingSec <= 0) {
            toast(context, "No active session to clear.")
            return
        }
        val client = app.mqttClientOrNull()
        if (client == null || !client.isConnected) {
            toast(context, "Reconnecting to server… Clear Time will fire when online.")
            app.queuePendingNotificationAction(PendingNotificationAction.ClearTime)
            app.kickReconnect()
            return
        }
        publishClearTime(app, client)
    }

    private fun publishAddTime(
        app: RentalAndroidApplication,
        client: com.rentalsystem.android.mqtt.StationMqttClient
    ) {
        val requestId = "r-${System.currentTimeMillis()}"
        app.claimTracker.startPending(requestId)
        app.appScope.launch {
            try {
                client.publishClaimRequest(requestId)
            } catch (t: Throwable) {
                Log.w(tag, "Add Time publish failed", t)
                app.claimTracker.reset()
            }
        }
    }

    private fun publishClearTime(
        app: RentalAndroidApplication,
        client: com.rentalsystem.android.mqtt.StationMqttClient
    ) {
        val requestId = "clear-${System.currentTimeMillis()}"
        app.appScope.launch {
            try {
                client.publishSessionClear(requestId)
            } catch (t: Throwable) {
                Log.w(tag, "Clear Time publish failed", t)
            }
        }
    }

    private fun toast(context: Context, message: String) {
        // Broadcast receivers can show toasts but the post must happen on the
        // main thread. Most paths into onReceive are already on Main, but the
        // explicit handler post keeps this safe regardless of caller.
        mainHandler.post { Toast.makeText(context, message, Toast.LENGTH_SHORT).show() }
    }

    companion object {
        const val ActionAddTime = "com.rentalsystem.android.action.NOTIFICATION_ADD_TIME"
        const val ActionClearTime = "com.rentalsystem.android.action.NOTIFICATION_CLEAR_TIME"

        /**
         * Application calls this from the MQTT-connected listener to drain
         * the pending action queued by an offline notification tap.
         */
        fun firePendingAction(
            app: RentalAndroidApplication,
            action: PendingNotificationAction
        ) {
            val receiver = SessionNotificationActionReceiver()
            val client = app.mqttClientOrNull() ?: return
            when (action) {
                PendingNotificationAction.AddTime -> receiver.publishAddTime(app, client)
                PendingNotificationAction.ClearTime -> receiver.publishClearTime(app, client)
            }
        }
    }
}

enum class PendingNotificationAction {
    AddTime,
    ClearTime
}
