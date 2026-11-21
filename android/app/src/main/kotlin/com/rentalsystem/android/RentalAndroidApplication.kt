package com.rentalsystem.android

import android.app.Application
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import com.rentalsystem.android.admin.TabletAdminConfigStore
import com.rentalsystem.android.apps.InstalledAppInventory
import com.rentalsystem.android.audio.AndroidCountdownWarningSoundPlayer
import com.rentalsystem.android.audio.CountdownWarningService
import com.rentalsystem.android.claims.ClaimStatus
import com.rentalsystem.android.claims.ClaimTracker
import com.rentalsystem.android.config.ClientConfig
import com.rentalsystem.android.config.BrokerConfig
import com.rentalsystem.android.credits.CreditAckService
import com.rentalsystem.android.credits.CreditDedupStore
import com.rentalsystem.android.discovery.MdnsDiscoveryListener
import com.rentalsystem.android.discovery.UdpDiscoveryListener
import com.rentalsystem.android.fresh.FreshCustomerUserResult
import com.rentalsystem.android.fresh.FreshCustomerUserSessionService
import com.rentalsystem.android.fresh.FreshUserResetGate
import com.rentalsystem.android.fresh.FreshUserResetService
import com.rentalsystem.android.mqtt.AbusePreventionConfigMessage
import com.rentalsystem.android.mqtt.AdminCommandMessage
import com.rentalsystem.android.mqtt.CreditMessage
import com.rentalsystem.android.mqtt.MqttConnectionStatus
import com.rentalsystem.android.mqtt.RateConfigMessage
import com.rentalsystem.android.mqtt.StationMqttClient
import com.rentalsystem.android.mqtt.TabletAdminConfigMessage
import com.rentalsystem.android.mqtt.TabletAppConfigMessage
import com.rentalsystem.android.mqtt.UpdateManifest
import com.rentalsystem.android.notifications.LogoutReminderTrigger
import com.rentalsystem.android.notifications.PendingNotificationAction
import com.rentalsystem.android.notifications.SessionNotificationActionReceiver
import com.rentalsystem.android.notifications.SessionNotificationManager
import com.rentalsystem.android.notifications.SessionNotificationService
import com.rentalsystem.android.sessions.SessionTracker
import com.rentalsystem.android.sessions.SessionTrackerState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// Process-scoped owner of the MQTT client and all stateful trackers.
//
// S25j fix: before this class existed, MainActivity owned the StationMqttClient
// as a per-instance field. Each Activity recreate (triggered by Lock Task Mode
// applying policies on the foreground activity, by orientation changes, or by
// other configuration churn) constructed a new HiveMQ client with the same
// "rental-station-{stationId}" identifier. Mosquitto enforces unique client
// ids, so it kicked the previous client off whenever a new one arrived; the
// kicked client's automatic-reconnect immediately rejoined, knocking the new
// client off; ping-pong at roughly 1 Hz. Functionally the broker delivered
// messages across the bounces, but the connectedListener republished the
// retained online status on every reconnect (noise on the broker bus) and
// the UI buttons flickered between "enabled" and "Offline" each cycle.
//
// Application.onCreate runs exactly once per process. Hoisting the client (and
// every other stateful object — SessionTracker, ClaimTracker, CreditAckService)
// here means one HiveMQ connection lives for the lifetime of the process,
// surviving every Activity recreate without re-identifying to the broker.
// Activities become thin observers that attach UI callbacks on onStart and
// detach on onStop; they do not own state.
class RentalAndroidApplication : Application() {

    private val tag = "RentalApp"

    private val supervisor = SupervisorJob()
    val appScope: CoroutineScope = CoroutineScope(supervisor + Dispatchers.Main.immediate)

    lateinit var clientConfig: ClientConfig
        private set
    lateinit var sessionTracker: SessionTracker
        private set
    lateinit var notificationManager: SessionNotificationManager
        private set
    lateinit var claimTracker: ClaimTracker
        private set
    lateinit var creditAckService: CreditAckService
        private set
    lateinit var mqttClient: StationMqttClient
        private set
    lateinit var freshUserResetService: FreshUserResetService
        private set
    lateinit var freshCustomerUserSessionService: FreshCustomerUserSessionService
        private set
    lateinit var tabletAdminConfigStore: TabletAdminConfigStore
        private set
    private lateinit var countdownWarningService: CountdownWarningService
    private lateinit var countdownWarningSoundPlayer: AndroidCountdownWarningSoundPlayer
    private lateinit var logoutReminderTrigger: LogoutReminderTrigger
    private lateinit var freshUserResetGate: FreshUserResetGate

    private var countdownWarningTicker: kotlinx.coroutines.Job? = null
    private var userForegroundReceiver: BroadcastReceiver? = null

    // Last-seen state cached so a newly-attached Activity (e.g. after a Lock
    // Task recreate) can render immediately without waiting for the next
    // server publish.
    @Volatile
    var lastConnectionStatus: MqttConnectionStatus? = null
        private set

    @Volatile
    var rateConfig: RateConfigMessage? = null
        private set

    @Volatile
    var tabletAppConfig: TabletAppConfigMessage? = null
        private set

    @Volatile
    var tabletAdminConfig: TabletAdminConfigMessage? = null
        private set

    var abusePreventionConfig: AbusePreventionConfigMessage? = null
        private set

    @Volatile
    var onConnectionStatusChanged: ((MqttConnectionStatus) -> Unit)? = null

    @Volatile
    var onCreditApplied: ((CreditMessage) -> Unit)? = null

    @Volatile
    var onRateConfigChanged: ((RateConfigMessage) -> Unit)? = null

    @Volatile
    var onTabletAppConfigChanged: ((TabletAppConfigMessage) -> Unit)? = null

    @Volatile
    var onAdminOpenRequested: (() -> Unit)? = null

    @Volatile
    var onUpdateManifestChanged: ((UpdateManifest) -> Unit)? = null

    @Volatile
    var updateManifest: UpdateManifest? = null
        private set

    // 
    // Single-slot rather than a queue because a customer who taps twice in
    // an offline window means "do this when we're back" once, not twice —
    // and replaying both would double-charge or double-clear once we
    // reconnected. The newer tap simply overwrites the older one.
    @Volatile
    var configError: Throwable? = null

    @Volatile
    var pendingNotificationAction: PendingNotificationAction? = null
        private set

    val hasMqttClient: Boolean
        get() = this::mqttClient.isInitialized

    fun mqttClientOrNull(): StationMqttClient? =
        if (hasMqttClient) mqttClient else null

    override fun onCreate() {
        super.onCreate()

        try {
            clientConfig = ClientConfig.loadFromAssets(this)
        } catch (t: Throwable) {
            Log.e(tag, "ClientConfig load failed; MQTT will not start.", t)
            configError = t
            return
        }

        // Auto-enable WiFi if it is off — the kiosk cannot function without
        // LAN connectivity to the MQTT broker. The CHANGE_WIFI_STATE permission
        // is declared in AndroidManifest.xml.
        ensureWifiEnabled()

        sessionTracker = SessionTracker()
        countdownWarningSoundPlayer = AndroidCountdownWarningSoundPlayer()
        countdownWarningService = CountdownWarningService(
            clientConfig.countdownWarning,
            countdownWarningSoundPlayer
        )
        logoutReminderTrigger = LogoutReminderTrigger(
            enabled = clientConfig.countdownWarning.logoutReminderEnabled,
            thresholdSec = clientConfig.countdownWarning.firstWarningSec
        )
        notificationManager = SessionNotificationManager(this).apply { ensureChannel() }
        tabletAdminConfigStore = TabletAdminConfigStore(this)
        tabletAdminConfig = tabletAdminConfigStore.load()
        freshUserResetService = FreshUserResetService(this, clientConfig.freshUserReset)
        freshUserResetGate = FreshUserResetGate()
        freshCustomerUserSessionService = FreshCustomerUserSessionService(this, clientConfig.freshUserReset)
        // Self-heal a leftover hidden launcher from any interrupted customer-user
        // cycle (setApplicationHidden is owner-scoped; a botched cycle can leave
        // the owner launcher hidden, and shell cannot restore it).
        appScope.launch(Dispatchers.IO) {
            try { freshCustomerUserSessionService.restoreOwnerLaunchers() } catch (_: Throwable) { }
        }
        // S27: process-scoped session listener. When the session transitions to
        // locked while the customer is using another app (because we released
        // the kiosk during an active session), pull the rental app back to the
        // foreground so MainActivity.onStart can re-engage Lock Task Mode via
        // KioskController.applySessionState(false). Without this hand-off,
        // a customer whose paid time runs out would keep using Chrome with no
        // visible signal — the rental app would still be running but
        // backgrounded.
        //
        // S31: also drive the persistent ongoing notification from this
        // listener. The notification is owned by a foreground service and uses
        // Android's native chronometer countdown, so Recents removal cannot
        // freeze the visible remaining time.
        sessionTracker.onAppStateChange = { state ->
            // S28f: realign the credit-dedup pointer against the server's
            // authoritative view of last applied seqno. Defends known-issue #4
            // root cause: if RentalServer's SQLite resets (dev wipe, station
            // re-registration), the server's next credit publish will use a
            // seqNo lower than our persisted lastProcessedSeqNo and every
            // subsequent credit would be silently dropped by CreditAckService's
            // strict-monotonic gate. realignFromSession is a no-op in steady
            // state (when session.lastCreditSeqNo >= local pointer) and only
            // pulls the pointer DOWN when we detect a server reset.
            if (state.hasSession && state.lastCreditSeqNo > 0) {
                appScope.launch {
                    try {
                        val realigned = creditAckService.realignFromSession(state.lastCreditSeqNo)
                        if (realigned) {
                            Log.w(
                                tag,
                                "credit-dedup realigned to ${state.lastCreditSeqNo} from session — server likely reset"
                            )
                        }
                    } catch (t: Throwable) {
                        Log.w(tag, "credit-dedup realignFromSession failed", t)
                    }
                }
            }

            handleFreshCustomerUserState(state)
            handleObservedSessionState(state)
        }
        // S28e: client-side Pending watchdog. Operator reported 2026-05-25
        // that tapping Insert Coin while the coin acceptor's 12 V supply was
        // unplugged left the tablet on "Requesting..." indefinitely — the
        // Windows rental client never publishes claim/result when its
        // acceptor-readiness check fails, so there's no message to drive the
        // tracker out of Pending. The only fix that day was force-stopping
        // the app. Watchdog must live on the Application (not MainActivity)
        // because Lock Task Mode recreates the Activity and would cancel
        // an Activity-scoped timer mid-flight.
        claimTracker = ClaimTracker().apply {
            onAppStateChange = { state ->
                when (state.status) {
                    ClaimStatus.Pending -> startPendingWatchdog()
                    else -> cancelPendingWatchdog()
                }
            }
        }
        creditAckService = CreditAckService(
            stationId = clientConfig.stationId,
            store = CreditDedupStore(this)
        ).apply {
            // Forward the application-level credit-applied notification to the
            // current observer (the foreground Activity). When no Activity is
            // attached the call is dropped — acceptable because the dedup
            // store already persisted the seqno, so a subsequent observer
            // won't double-count.
            onNewCreditApplied = { message ->
                onCreditApplied?.invoke(message)
            }
        }

        // P1 (backlog): resolve broker host via mDNS / DNS-SD (NsdManager) when
        // config has broker.host="auto". Multicast reaches Wi-Fi clients, unlike
        // the server's UDP broadcast which routers don't forward to Wi-Fi — the
        // production dry-run finding. Runs once at process start before building
        // the MQTT client, on a background thread (NsdManager delivers callbacks
        // on its own thread, so this bounded join won't deadlock).
        startMqttAfterDiscovery()
        registerUserForegroundReceiver()
    }

    private fun startMqttAfterDiscovery() {
        appScope.launch(Dispatchers.IO) {
            val resolvedConfig = resolveBrokerConfigForStartup()
            val client = StationMqttClient(resolvedConfig).apply {
            onStatusChange = { status ->
                val previouslyOnline = lastConnectionStatus?.connected == true
                emitAppStatus(status)
                // Offline→online edge: drain any notification tap that landed
                // during the disconnect window. We check the edge rather than
                // just `status.connected` so we don't replay the action on
                // every status emission while online (e.g. the post-CONNACK
                // "Connected" message followed later by the redundant
                // emitStatus from publishStationStatus on reconnect).
                if (status.connected && !previouslyOnline) {
                    publishInstalledAppInventory()
                    pendingNotificationAction?.let { action ->
                        pendingNotificationAction = null
                        Log.i(tag, "draining pending notification action: $action")
                        SessionNotificationActionReceiver.firePendingAction(this@RentalAndroidApplication, action)
                    }
                }
            }
            onSessionReceived = { message ->
                // sessionTracker.apply is synchronized internally; safe from the
                // Netty thread the HiveMQ client uses for incoming PUBLISH callbacks.
                sessionTracker.apply(message)
            }
            onClaimResultReceived = { message ->
                claimTracker.apply(message)
            }
            onCreditReceived = { message ->
                // creditAckService.process is a suspend function — it has to await
                // the QoS 1 publish of the credit/ack. Run it on the application
                // scope so it survives Activity recreates mid-process.
                appScope.launch {
                    try {
                        creditAckService.process(message) { seqNo ->
                            publishCreditAck(seqNo)
                        }
                    } catch (t: Throwable) {
                        Log.w(tag, "credit/ack publish failed", t)
                    }
                }
            }
            onRateConfigReceived = { message ->
                rateConfig = message
                onRateConfigChanged?.invoke(message)
            }
            onTabletAppConfigReceived = { message ->
                tabletAppConfig = message
                freshUserResetService.updateTabletAppConfig(message)
                freshCustomerUserSessionService.updateTabletAppConfig(message)
                handleFreshCustomerUserState(sessionTracker.getCurrentState())
                onTabletAppConfigChanged?.invoke(message)
            }
            onTabletAdminConfigReceived = { message ->
                tabletAdminConfig = message
                tabletAdminConfigStore.save(message)
            }
            onAbusePreventionConfigReceived = { message ->
                abusePreventionConfig = message
            }
            onUpdateManifestReceived = { manifest ->
                updateManifest = manifest
                onUpdateManifestChanged?.invoke(manifest)
            }
            onAdminOpenReceived = {
                onAdminOpenRequested?.invoke()
            }
            onAdminCommandReceived = { message ->
                handleAdminCommand(message)
            }
            onAppResetReceived = { message ->
                // Staff one-tap reset: clear (log out) the picked app packages.
                // appScope so it survives Activity recreates; service runs on IO.
                // Independent of session state — staff can reset whether the
                // tablet is locked or active.
                appScope.launch {
                    try {
                        val summary = freshUserResetService.clearPackages(message.packages)
                        Log.i(tag, "staff app-reset for ${message.packages}: $summary")
                    } catch (t: Throwable) {
                        Log.w(tag, "staff app-reset failed for ${message.packages}", t)
                    }
                }
            }
        }
            mqttClient = client

        // Single connect for the lifetime of the process. HiveMQ's
        // automaticReconnectWithDefaultConfig handles broker outages
        // transparently within this same instance.
        //
        // Expected one-time startup transient (~5 s after the first connect of
        // a fresh process that follows a killed predecessor): the broker may
        // still have an open TCP record for the previous process and, on
        // detecting the duplicate cleanSession=false client-id, fire that
        // previous session's LWT (publishing the offline retained shape) while
        // promoting our new TCP socket. HiveMQ's automatic-reconnect picks the
        // connection back up within ~8 s and onConnectedReconnect re-publishes
        // the online retained payload, so observers see one offline blip then
        // back to online. In a true production cold-start (factory-reset
        // tablet, no prior process) there is no prior session to evict and
        // the transient does not occur.
        try {
            client.connect()
            publishInstalledAppInventory()
        } catch (t: Throwable) {
            Log.w(tag, "initial connect failed; auto-reconnect will retry", t)
        }
    }
    }

    private suspend fun resolveBrokerConfigForStartup(): ClientConfig {
        if (!isAutoDiscovery(clientConfig.broker.host)) {
            return clientConfig
        }

        var attempt = 1
        while (true) {
            emitAppStatus(
                MqttConnectionStatus(
                    connected = false,
                    message = "Finding server on WiFi...",
                    detail = "Auto discovery attempt $attempt"
                )
            )

            val discovered = discoverBroker()
            if (discovered != null) {
                Log.i(tag, "Discovered broker at ${discovered.brokerHost}:${discovered.brokerPort} via ${discovered.source}.")
                return clientConfig.copy(
                    broker = clientConfig.broker.copy(
                        host = discovered.brokerHost,
                        port = discovered.brokerPort
                    )
                )
            }

            Log.w(tag, "Auto-discovery found no server; retrying.")
            emitAppStatus(
                MqttConnectionStatus(
                    connected = false,
                    message = "Finding server on WiFi...",
                    detail = "No discovery reply yet; retrying"
                )
            )
            delay(5_000)
            attempt++
        }
    }

    private fun emitAppStatus(status: MqttConnectionStatus) {
        lastConnectionStatus = status
        onConnectionStatusChanged?.invoke(status)
    }

    private fun handleAdminCommand(message: AdminCommandMessage) {
        if (message.stationId.isNotBlank() && message.stationId != clientConfig.stationId) {
            appScope.launch {
                publishCommandAckSafe(
                    requestId = message.requestId,
                    result = "rejected",
                    detail = "station mismatch"
                )
            }
            return
        }

        if (message.command != CommandCustomerLogout) {
            appScope.launch {
                publishCommandAckSafe(
                    requestId = message.requestId,
                    result = "rejected",
                    detail = "unknown command: ${message.command}"
                )
            }
            return
        }

        if (clientConfig.deviceType != "tab") {
            appScope.launch {
                publishCommandAckSafe(
                    requestId = message.requestId,
                    result = "rejected",
                    detail = "customer_logout is tablet-only"
                )
            }
            return
        }

        appScope.launch(Dispatchers.IO) {
            if (freshCustomerUserSessionService.isProfileOwnerCustomerUser) {
                publishCommandAckSafe(
                    requestId = message.requestId,
                    result = "acknowledged",
                    detail = "customer user logout requested; owner will remove user"
                )
            }

            val result = try {
                freshCustomerUserSessionService.handleStaffCustomerLogout()
            } catch (t: Throwable) {
                FreshCustomerUserResult.Failed("${t.javaClass.simpleName}: ${t.message}")
            }

            Log.i(tag, "admin customer_logout result: ${describeFreshCustomerUserResult(result)}")

            if (!freshCustomerUserSessionService.isProfileOwnerCustomerUser) {
                val (ackResult, detail) = commandAckForFreshCustomerUserResult(result)
                publishCommandAckSafe(message.requestId, ackResult, detail)
            }
        }
    }

    private suspend fun publishCommandAckSafe(
        requestId: String,
        result: String,
        detail: String
    ) {
        if (requestId.isBlank()) {
            return
        }

        try {
            mqttClient.publishCommandAck(requestId, result, detail)
        } catch (t: Throwable) {
            Log.w(tag, "admin command ack publish failed", t)
        }
    }

    private fun commandAckForFreshCustomerUserResult(result: FreshCustomerUserResult): Pair<String, String> =
        when (result) {
            FreshCustomerUserResult.Disabled ->
                "rejected" to "customer-user mode disabled"
            is FreshCustomerUserResult.Failed ->
                "rejected" to result.reason
            is FreshCustomerUserResult.Noop ->
                "completed" to result.reason
            FreshCustomerUserResult.RemovedCustomerUser ->
                "completed" to "customer user removed"
            FreshCustomerUserResult.CustomerUserWipeRequested ->
                "acknowledged" to "customer user wipe requested"
            is FreshCustomerUserResult.LogoutRequested ->
                "acknowledged" to "customer user logout requested: ${result.resultCode}"
            is FreshCustomerUserResult.PackagesProvisioned ->
                "completed" to "packages provisioned"
            is FreshCustomerUserResult.SwitchedToCustomerUser ->
                "completed" to "switched to customer user"
        }

    private fun describeFreshCustomerUserResult(result: FreshCustomerUserResult): String =
        when (result) {
            FreshCustomerUserResult.Disabled -> "disabled"
            is FreshCustomerUserResult.Failed -> "failed: ${result.reason}"
            is FreshCustomerUserResult.Noop -> "noop: ${result.reason}"
            FreshCustomerUserResult.RemovedCustomerUser -> "removed customer user"
            FreshCustomerUserResult.CustomerUserWipeRequested -> "customer user wipe requested"
            is FreshCustomerUserResult.LogoutRequested -> "logout requested: ${result.resultCode}"
            is FreshCustomerUserResult.PackagesProvisioned ->
                "packages provisioned installed=${result.installed} failed=${result.failed}"
            is FreshCustomerUserResult.SwitchedToCustomerUser ->
                "switched to customer user for session ${result.sessionId}"
        }

    private fun publishInstalledAppInventory() {
        if (clientConfig.deviceType != "tab") {
            return
        }

        appScope.launch(Dispatchers.IO) {
            try {
                mqttClient.publishAppInventory(InstalledAppInventory.collect(this@RentalAndroidApplication))
            } catch (t: Throwable) {
                Log.w(tag, "app inventory publish failed", t)
            }
        }
    }

    private fun bringMainActivityToFront() {
        try {
            val launch = packageManager.getLaunchIntentForPackage(packageName)
                ?.addFlags(
                    android.content.Intent.FLAG_ACTIVITY_NEW_TASK or
                        android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP or
                        android.content.Intent.FLAG_ACTIVITY_REORDER_TO_FRONT
                )
                ?: return
            startActivity(launch)
        } catch (t: Throwable) {
            Log.w(tag, "bringMainActivityToFront failed", t)
        }
    }

    fun handleObservedSessionState(state: SessionTrackerState) {
        val active = state.hasSession && !state.locked && state.effectiveRemainingSec > 0
        Log.d(tag, "session observed: locked=${state.locked} active=$active sessionId=${state.sessionId} remaining=${state.effectiveRemainingSec}")

        // Must reach the gate for BOTH active and locked states:
        //   - Active: sets activeSessionId so the gate knows this session was observed active
        //   - Locked: checks activeSessionId match and fires cleanup exactly once
        val cleanupSessionId = freshUserResetGate.cleanupSessionIdIfEnded(state)

        if (active) {
            startCountdownWarningTicker()
            if (!freshCustomerUserSessionService.isEnabled ||
                freshCustomerUserSessionService.isProfileOwnerCustomerUser
            ) {
                SessionNotificationService.start(this, state)
            }
            return
        }

        stopCountdownWarningTicker(state)
        if (cleanupSessionId != null && !freshCustomerUserSessionService.isEnabled) {
            Log.i(tag, "deep-freeze triggered for session $cleanupSessionId")
            appScope.launch(Dispatchers.IO) {
                try {
                    val summary = freshUserResetService.runAfterSessionEnded(cleanupSessionId)
                    Log.i(tag, "deep-freeze complete: $summary")
                } catch (t: Throwable) {
                    Log.w(tag, "fresh-user cleanup failed for session $cleanupSessionId", t)
                }
            }
        }
        SessionNotificationService.stop(this)
        notificationManager.dismiss()
        if (state.hasSession && !freshCustomerUserSessionService.isProfileOwnerCustomerUser) {
            bringMainActivityToFront()
        }
    }

    private fun startCountdownWarningTicker() {
        if (countdownWarningTicker?.isActive == true) {
            return
        }

        countdownWarningTicker = appScope.launch {
            try {
                while (true) {
                    val state = sessionTracker.getCurrentState()
                    if (!state.hasSession || state.locked || state.effectiveRemainingSec <= 0) {
                        countdownWarningService.update(state)
                        logoutReminderTrigger.update(state)
                        break
                    }

                    countdownWarningService.update(state)
                    if (logoutReminderTrigger.update(state)) {
                        // HyperOS can suppress heads-up notifications while a
                        // customer app is fullscreen, so use all three signals:
                        // notification for normal Android devices, Toast for
                        // a lightweight overlay, and bring Slotify forward so
                        // the in-app warning banner is visible at least once.
                        notificationManager.showLogoutReminder(clientConfig.countdownWarning.logoutReminderMessage)
                        showToast(clientConfig.countdownWarning.logoutReminderMessage)
                        bringMainActivityToFront()
                    }
                    delay(1000)
                }
            } finally {
                countdownWarningTicker = null
            }
        }
    }

    private fun stopCountdownWarningTicker(state: SessionTrackerState) {
        countdownWarningTicker?.cancel()
        countdownWarningTicker = null
        countdownWarningService.update(state)
        logoutReminderTrigger.update(state)
    }

    private fun handleFreshCustomerUserState(state: SessionTrackerState) {
        if (!freshCustomerUserSessionService.isEnabled) {
            return
        }

        appScope.launch(Dispatchers.IO) {
            val result = try {
                freshCustomerUserSessionService.handleObservedSessionState(state) {
                    SessionNotificationService.stop(this@RentalAndroidApplication)
                    notificationManager.dismiss()
                    mqttClient.disconnectGracefully()
                }
            } catch (t: Throwable) {
                Log.w(tag, "fresh customer-user state handling failed", t)
                FreshCustomerUserResult.Failed("${t.javaClass.simpleName}: ${t.message}")
            }

            when (result) {
                FreshCustomerUserResult.Disabled -> Unit
                is FreshCustomerUserResult.Noop -> Log.d(tag, "fresh customer-user noop: ${result.reason}")
                is FreshCustomerUserResult.SwitchedToCustomerUser -> {
                    Log.i(tag, "fresh customer-user switched for session ${result.sessionId}")
                }
                is FreshCustomerUserResult.PackagesProvisioned -> {
                    Log.i(
                        tag,
                        "fresh customer-user provisioned packages: installed=${result.installed}, failed=${result.failed}"
                    )
                    publishInstalledAppInventory()
                    tabletAppConfig?.let { config ->
                        mainHandler.post { onTabletAppConfigChanged?.invoke(config) }
                    }
                }
                FreshCustomerUserResult.RemovedCustomerUser -> {
                    Log.i(tag, "fresh customer-user removed after session end")
                }
                FreshCustomerUserResult.CustomerUserWipeRequested -> {
                    Log.i(tag, "fresh customer-user wipe requested")
                }
                is FreshCustomerUserResult.LogoutRequested -> {
                    Log.i(tag, "fresh customer-user logout requested: result=${result.resultCode}")
                }
                is FreshCustomerUserResult.Failed -> {
                    Log.w(tag, "fresh customer-user failed: ${result.reason}")
                }
            }
        }
    }

    // S28e Pending watchdog. ClaimTracker.onAppStateChange fires this on
    // every Pending entry; transitions to any other status cancel via
    // cancelPendingWatchdog. The 12 s figure is a guess based on typical
    // broker round-trip + acceptor-readiness check time — tune only with
    // a reason. Re-checks the tracker's status after the delay so we never
    // overwrite a Granted/Denied that landed during the wait window
    // (belt-and-suspenders against a late cancel).
    @Volatile
    private var pendingWatchdog: Job? = null

    private val mainHandler = Handler(Looper.getMainLooper())

    private fun startPendingWatchdog() {
        pendingWatchdog?.cancel()
        pendingWatchdog = appScope.launch {
            delay(12_000)
            if (claimTracker.getCurrentState().status == ClaimStatus.Pending) {
                Log.w(tag, "Pending watchdog fired — server never published claim/result; transitioning to OfflineTimeout")
                claimTracker.markOfflineTimeout()
                // Application-level Toast so the customer gets feedback even
                // when the rental app is backgrounded (notification-tap path
                // after S28a auto-minimize). The previous MainActivity-only
                // Toast was invisible to a user looking at the system launcher.
                showToast("Coin slot offline — try again.")
            }
        }
    }

    /**
     * Show a Toast from any thread. Application context is used so the Toast
     * works even when no Activity is attached (e.g. after S28a auto-minimize
     * sent the rental Activity to the background while the customer's session
     * is active).
     */
    private fun showToast(message: String) {
        mainHandler.post { Toast.makeText(this, message, Toast.LENGTH_LONG).show() }
    }

    private fun cancelPendingWatchdog() {
        pendingWatchdog?.cancel()
        pendingWatchdog = null
    }

    /**
     * Stash a notification-action tap that arrived while MQTT was offline.
     * The MQTT status listener replays it on the next offline→online edge.
     * Single-slot — a newer tap supersedes any older one.
     */
    fun queuePendingNotificationAction(action: PendingNotificationAction) {
        pendingNotificationAction = action
    }

    /**
     * Force HiveMQ out of its automatic-reconnect backoff sleep and try to
     * connect now. Safe to call when already connected — StationMqttClient.connect()
     * is idempotent (it just refreshes the subscription + retained online slot).
     *
     * Called by the notification action receiver when a tap lands during a
     * disconnect window: pairing the queue with a kick shortens the wait
     * from "whenever exponential backoff next fires" to "as soon as the
     * network is actually reachable."
     *
     * Skips entirely when HiveMQ is mid-reconnect (CONNECTING / CONNECTING_RECONNECT
     * / DISCONNECTED_RECONNECT) — calling connect() in those states throws
     * MqttClientStateException ("already connected or connecting") and wastes
     * a stack trace in the log. The in-flight auto-reconnect reaches the same
     * end state on its own; the queued notification action drains correctly
     * either way (via the onStatusChange offline->online edge handler).
     *
     * Reported 2026-05-25 during S28c bench-test: every notification tap that
     * landed while HiveMQ was already mid-blip produced two "MQTT client is
     * already connected or connecting" warnings even though the end-to-end
     * behavior was correct. This guard suppresses the noise.
     */
    fun kickReconnect() {
        val client = mqttClientOrNull()
        if (client == null) {
            Log.d(tag, "kickReconnect skipped - MQTT client is still waiting for broker discovery.")
            return
        }
        if (!mqttClient.isIdleForManualConnect) {
            Log.d(tag, "kickReconnect skipped — HiveMQ already in-flight (state=${mqttClient.connectionStateName})")
            return
        }
        appScope.launch {
            try {
                mqttClient.connect()
            } catch (t: Throwable) {
                Log.w(tag, "kickReconnect failed; auto-reconnect will retry", t)
            }
        }
    }

    private fun discoverBroker(): BrokerDiscoveryResult? {
        var mdnsResult: MdnsDiscoveryListener.DiscoveryResult? = null
        val mdnsThread = Thread {
            mdnsResult = MdnsDiscoveryListener.discover(this@RentalAndroidApplication)
        }
        mdnsThread.start()
        mdnsThread.join(12_000) // MdnsDiscoveryListener has a ~10 s internal timeout
        mdnsResult?.let {
            return BrokerDiscoveryResult(it.brokerHost, it.brokerPort, source = "mDNS")
        }

        var udpResult: UdpDiscoveryListener.DiscoveryResult? = null
        val udpThread = Thread {
            udpResult = UdpDiscoveryListener.discover()
        }
        udpThread.start()
        udpThread.join(13_000) // UdpDiscoveryListener waits up to 12 s for a broadcast
        udpResult?.let {
            return BrokerDiscoveryResult(it.brokerHost, it.brokerPort, source = "UDP broadcast")
        }

        return null
    }

    private data class BrokerDiscoveryResult(
        val brokerHost: String,
        val brokerPort: Int,
        val source: String
    )

    private fun registerUserForegroundReceiver() {
        if (userForegroundReceiver != null) {
            return
        }

        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                if (intent.action != Intent.ACTION_USER_FOREGROUND) {
                    return
                }

                if (!freshCustomerUserSessionService.isEnabled ||
                    freshCustomerUserSessionService.isProfileOwnerCustomerUser
                ) {
                    return
                }

                Log.i(tag, "Owner user returned to foreground; recycling stored customer user if present.")
                appScope.launch(Dispatchers.IO) {
                    try {
                        val result = freshCustomerUserSessionService.removeStoredCustomerUserAfterOwnerReturn()
                        Log.i(tag, "owner foreground customer-user cleanup: ${describeFreshCustomerUserResult(result)}")
                    } catch (t: Throwable) {
                        Log.w(tag, "owner foreground customer-user cleanup failed", t)
                    }
                    kickReconnect()
                }
            }
        }

        val filter = IntentFilter(Intent.ACTION_USER_FOREGROUND)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            registerReceiver(receiver, filter)
        }
        userForegroundReceiver = receiver
    }

    // ---------- WiFi auto-enable ----------

    private fun ensureWifiEnabled() {
        val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return

        try {
            if (!wifi.isWifiEnabled) {
                Log.w(tag, "WiFi is off — enabling for kiosk operation.")
                @Suppress("DEPRECATION")
                wifi.isWifiEnabled = true
            }
        } catch (t: Throwable) {
            // Best-effort — some ROMs may block this despite the manifest permission.
            // If WiFi stays off, the MQTT client will surface "offline" and the
            // operator needs to enable WiFi manually through the admin unlock flow.
            Log.e(tag, "Failed to enable WiFi automatically.", t)
        }
    }

    companion object {
        private const val CommandCustomerLogout = "customer_logout"

        private fun isAutoDiscovery(host: String): Boolean {
            val trimmed = host.trim()
            return trimmed.isEmpty() ||
                   trimmed.equals(BrokerConfig.AUTO_HOST_SENTINEL, ignoreCase = true)
        }
    }
}
