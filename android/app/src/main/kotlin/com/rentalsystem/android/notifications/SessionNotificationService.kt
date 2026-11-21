package com.rentalsystem.android.notifications

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import android.util.Log
import com.rentalsystem.android.RentalAndroidApplication
import com.rentalsystem.android.sessions.SessionTrackerState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

private fun SessionTrackerState.isActiveSession(): Boolean =
    hasSession && !locked && effectiveRemainingSec > 0

class SessionNotificationService : Service() {
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var notificationManager: SessionNotificationManager
    private var ticker: Job? = null
    private var snapshot: Snapshot? = null

    override fun onCreate() {
        super.onCreate()
        notificationManager = SessionNotificationManager(this).apply { ensureChannel() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ActionStop) {
            stopForegroundAndSelf()
            return START_NOT_STICKY
        }

        intent?.readSnapshot()?.let { snapshot = it }
        val state = activeTrackerState() ?: snapshot?.toState()
        if (state == null || !state.isActiveSession()) {
            stopForegroundAndSelf()
            return START_NOT_STICKY
        }

        if (!startOrUpdateForeground(state)) {
            stopSelf()
            return START_NOT_STICKY
        }
        ensureTicker()
        return START_REDELIVER_INTENT
    }

    override fun onDestroy() {
        ticker?.cancel()
        ticker = null
        stopForegroundSafely()
        serviceScope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun ensureTicker() {
        if (ticker?.isActive == true) {
            return
        }

        ticker = serviceScope.launch {
            while (isActive) {
                val app = application as? RentalAndroidApplication
                val trackerState = try {
                    app?.sessionTracker?.getCurrentState()
                } catch (_: UninitializedPropertyAccessException) {
                    null
                }
                val state = trackerState?.takeIf { it.isActiveSession() } ?: snapshot?.toState()
                if (state == null || !state.isActiveSession()) {
                    trackerState?.let { app?.handleObservedSessionState(it) }
                    stopForegroundAndSelf()
                    break
                }

                delay(1000)
            }
        }
    }

    private fun startOrUpdateForeground(state: SessionTrackerState): Boolean {
        val notification = notificationManager.buildNotification(state)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startForeground(
                    SessionNotificationManager.NotificationId,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED
                )
            } else {
                @Suppress("DEPRECATION")
                startForeground(SessionNotificationManager.NotificationId, notification)
            }
            return true
        } catch (error: SecurityException) {
            // systemExempted foreground services are valid only while this app is
            // Device Owner. During bench recovery the app may temporarily run
            // without Device Owner; keep the UI alive and show a normal
            // notification instead of crashing on the retained active session.
            Log.w(Tag, "startForeground denied; falling back to direct notification", error)
            notificationManager.showOrUpdate(state)
            return false
        }
    }

    private fun activeTrackerState(): SessionTrackerState? {
        return try {
            val app = application as? RentalAndroidApplication ?: return null
            app.sessionTracker.getCurrentState().takeIf { it.isActiveSession() }
        } catch (_: UninitializedPropertyAccessException) {
            null
        }
    }

    private fun stopForegroundAndSelf() {
        stopForegroundSafely()
        stopSelf()
    }

    private fun stopForegroundSafely() {
        try {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } catch (error: RuntimeException) {
            Log.w(Tag, "stopForeground failed", error)
        }
    }

    private fun Intent.readSnapshot(): Snapshot? {
        val sessionId = getStringExtra(ExtraSessionId) ?: return null
        val stationId = getStringExtra(ExtraStationId) ?: return null
        val deviceType = getStringExtra(ExtraDeviceType) ?: return null
        val remainingSec = getLongExtra(ExtraRemainingSec, 0L)
        if (remainingSec <= 0L) {
            return null
        }

        return Snapshot(
            sessionId = sessionId,
            stationId = stationId,
            deviceType = deviceType,
            lastCreditSeqNo = getLongExtra(ExtraLastCreditSeqNo, 0L),
            serverUpdatedAt = getStringExtra(ExtraServerUpdatedAt) ?: "",
            endElapsedRealtime = SystemClock.elapsedRealtime() + (remainingSec * 1000L)
        )
    }

    private data class Snapshot(
        val sessionId: String,
        val stationId: String,
        val deviceType: String,
        val lastCreditSeqNo: Long,
        val serverUpdatedAt: String,
        val endElapsedRealtime: Long
    ) {
        fun toState(): SessionTrackerState? {
            val remainingSec = ((endElapsedRealtime - SystemClock.elapsedRealtime()) / 1000L)
                .coerceAtLeast(0L)
            if (remainingSec <= 0L) {
                return null
            }

            return SessionTrackerState(
                hasSession = true,
                sessionId = sessionId,
                stationId = stationId,
                deviceType = deviceType,
                locked = false,
                effectiveRemainingSec = remainingSec,
                lastCreditSeqNo = lastCreditSeqNo,
                serverUpdatedAt = serverUpdatedAt
            )
        }
    }

    companion object {
        private const val Tag = "RentalNotifService"
        private const val ActionStart = "com.rentalsystem.android.action.SESSION_NOTIFICATION_START"
        private const val ActionStop = "com.rentalsystem.android.action.SESSION_NOTIFICATION_STOP"
        private const val ExtraSessionId = "sessionId"
        private const val ExtraStationId = "stationId"
        private const val ExtraDeviceType = "deviceType"
        private const val ExtraRemainingSec = "remainingSec"
        private const val ExtraLastCreditSeqNo = "lastCreditSeqNo"
        private const val ExtraServerUpdatedAt = "serverUpdatedAt"

        fun start(context: Context, state: SessionTrackerState) {
            val intent = Intent(context, SessionNotificationService::class.java)
                .setAction(ActionStart)
                .putExtra(ExtraSessionId, state.sessionId)
                .putExtra(ExtraStationId, state.stationId)
                .putExtra(ExtraDeviceType, state.deviceType)
                .putExtra(ExtraRemainingSec, state.effectiveRemainingSec)
                .putExtra(ExtraLastCreditSeqNo, state.lastCreditSeqNo)
                .putExtra(ExtraServerUpdatedAt, state.serverUpdatedAt)

            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
            } catch (error: RuntimeException) {
                Log.w(Tag, "startForegroundService failed; falling back to direct notification", error)
                SessionNotificationManager(context).apply {
                    ensureChannel()
                    showOrUpdate(state)
                }
            }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, SessionNotificationService::class.java))
        }
    }
}
