package com.rentalsystem.android.mqtt

import android.os.SystemClock
import android.util.Log
import com.hivemq.client.mqtt.MqttClient
import com.hivemq.client.mqtt.MqttClientState
import com.hivemq.client.mqtt.MqttGlobalPublishFilter
import com.hivemq.client.mqtt.datatypes.MqttQos
import com.hivemq.client.mqtt.mqtt3.Mqtt3AsyncClient
import com.hivemq.client.mqtt.mqtt3.message.publish.Mqtt3Publish
import com.rentalsystem.android.config.ClientConfig
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.concurrent.CompletableFuture
import kotlinx.coroutines.future.await
import kotlinx.serialization.json.Json

// Owns the MQTT connection for a single station (Android tablet).
// Mirrors RentalClient.Windows/Mqtt/StationMqttClient.cs.
//
// S25a (connect + status + LWT):
//   - registers an LWT on `rental/station/{stationId}/status` (retained, online=false)
//   - publishes the online retained status overwriting the LWT slot on CONNACK
//   - disconnectGracefully() publishes the offline retained substitute before
//     issuing a normal DISCONNECT (MQTT 3.1.1 graceful DISCONNECT discards the LWT).
//
// S25b (this slice):
//   - HiveMQ automatic-reconnect handles broker outages transparently. On every
//     reconnect a connectedListener republishes the retained online status -
//     necessary because the LWT overwrote our retained slot with offline during
//     the disconnect window.
//   - Subscribes to `rental/station/{stationId}/session` at QoS 1. With
//     cleanSession=false the broker preserves the subscription across reconnects;
//     incoming messages route to onSessionReceived via the global publishes
//     callback (which is independent of which connect cycle subscribed).
//
// S25c adds claim/request, claim/result, and claim/end so the tablet can open
// and close the same coin-insertion window as the Windows client.
//
// S25d adds credit subscribe + ACK publish.
//
// S25e adds retained config/rates subscribe for locked-rate display.
//
// S25f adds session/clear publish.
class StationMqttClient(private val config: ClientConfig) {

    private val tag = "RentalMqtt"
    private val utcFormatter = DateTimeFormatter
        .ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'")
        .withZone(ZoneOffset.UTC)

    private val client: Mqtt3AsyncClient = MqttClient.builder()
        .useMqttVersion3()
        .identifier("rental-station-${config.stationId}")
        .serverHost(config.broker.host)
        .serverPort(config.broker.port)
        // Reconnect backoff: 1 s initial, max 30 s (vs HiveMQ's 2 min default).
        // The default 2 min cap meant a transient WiFi blip during an Add Time /
        // Clear Time notification tap silently dropped the action — reported
        // 2026-05-24 "cant add and clear time mqtt offline".
        .automaticReconnect()
            .initialDelay(1, java.util.concurrent.TimeUnit.SECONDS)
            .maxDelay(30, java.util.concurrent.TimeUnit.SECONDS)
            .applyAutomaticReconnect()
        .addConnectedListener { onConnectedReconnect() }
        .addDisconnectedListener { ctx ->
            // HiveMQ's automaticReconnect handles re-establishment; we just surface
            // the state change for the UI badge.
            emitStatus(
                connected = false,
                message = "Disconnected from broker (auto-reconnect in progress).",
                detail = ctx.cause?.message
            )
        }
        .buildAsync()

    private var bootElapsedMs: Long = 0L

    // Tracks whether the explicit initial connect() has completed at least once.
    // The connectedListener fires on every successful connect, including the
    // initial CONNACK that connect() itself awaits - we suppress the first
    // republish to avoid a duplicate publish race against the explicit one in
    // connect().
    @Volatile
    private var initialConnectComplete = false

    // Lenient on inbound, strict (drop nulls) on outbound - matches the C#
    // PropertyNameCaseInsensitive + DefaultIgnoreCondition.WhenWritingNull pair.
    private val outboundJson = Json { encodeDefaults = false; explicitNulls = false }
    private val inboundJson = Json { ignoreUnknownKeys = true; isLenient = true }

    @Volatile
    var onStatusChange: ((MqttConnectionStatus) -> Unit)? = null

    @Volatile
    var onSessionReceived: ((SessionMessage) -> Unit)? = null

    @Volatile
    var onClaimResultReceived: ((ClaimResultMessage) -> Unit)? = null

    @Volatile
    var onCreditReceived: ((CreditMessage) -> Unit)? = null

    @Volatile
    var onRateConfigReceived: ((RateConfigMessage) -> Unit)? = null

    @Volatile
    var onAbusePreventionConfigReceived: ((AbusePreventionConfigMessage) -> Unit)? = null

    @Volatile
    var onTabletAppConfigReceived: ((TabletAppConfigMessage) -> Unit)? = null

    @Volatile
    var onTabletAdminConfigReceived: ((TabletAdminConfigMessage) -> Unit)? = null

    @Volatile
    var onUpdateManifestReceived: ((UpdateManifest) -> Unit)? = null

    @Volatile
    var onAdminOpenReceived: (() -> Unit)? = null

    @Volatile
    var onAdminCommandReceived: ((AdminCommandMessage) -> Unit)? = null

    var onAppResetReceived: ((AppResetCommandMessage) -> Unit)? = null

    val stationId: String get() = config.stationId
    val brokerEndpoint: String get() = "${config.broker.host}:${config.broker.port}"

    val isConnected: Boolean
        get() = client.state.isConnected

    /**
     * True only when HiveMQ has nothing in flight — no active connect and no
     * scheduled/in-progress automatic reconnect. The kickReconnect path on
     * Application uses this to skip calling connect() during a reconnect that's
     * already running (e.g. CONNECTING / CONNECTING_RECONNECT / DISCONNECTED_RECONNECT),
     * which would otherwise throw MqttClientStateException and pollute the log.
     *
     * Reported 2026-05-25 during the s28c offline-tap drain bench-test:
     * "kickReconnect failed; MQTT client is already connected or connecting"
     * warnings every time a notification tap fired while HiveMQ was mid-blip.
     * The end-to-end behavior was always correct (the queued action drained
     * on HiveMQ's auto-reconnect anyway), but the warnings were noise.
     */
    val isIdleForManualConnect: Boolean
        get() = client.state == MqttClientState.DISCONNECTED

    /** Diagnostic helper: current HiveMQ state name for log lines. */
    val connectionStateName: String
        get() = client.state.name

    /**
     * Connects to the broker, subscribes to the station-scoped session topic,
     * publishes the retained online status, and returns once all three succeed.
     * Idempotent: re-running while already connected refreshes the subscription
     * and retained online slot.
     */
    suspend fun connect() {
        if (isConnected) {
            subscribeAll()
            publishStationStatus(online = true)
            initialConnectComplete = true
            return
        }

        bootElapsedMs = SystemClock.elapsedRealtime()
        val lwtBytes = serializeStatus(online = false, includeBootFields = false)

        emitStatus(false, "Connecting to $brokerEndpoint as ${config.stationId}...")

        try {
            // Register the global publishes callback BEFORE subscribing so any
            // retained payload delivered immediately on subscribe is routed
            // rather than dropped on the floor.
            client.publishes(MqttGlobalPublishFilter.SUBSCRIBED) { publish ->
                dispatchIncoming(publish.topic.toString(), publish.payloadAsBytes)
            }

            val connBuilder = client.connectWith()
                .cleanSession(false)
                .keepAlive(30)
                .willPublish()
                    .topic(Topics.stationStatus(config.stationId))
                    .payload(lwtBytes)
                    .qos(MqttQos.AT_LEAST_ONCE)
                    .retain(true)
                    .applyWillPublish()

            // Only authenticate when both fields are non-blank — blank means
            // "anonymous dev broker" (S2 Mosquitto default, no auth).
            if (config.broker.hasCredentials) {
                connBuilder.simpleAuth()
                    .username(config.broker.username!!)
                    .password(config.broker.password!!.toByteArray())
                    .applySimpleAuth()
            }

            connBuilder.send()
                .await()

            subscribeAll()
            publishStationStatus(online = true)
            initialConnectComplete = true
            emitStatus(true, "Connected as ${config.stationId} to $brokerEndpoint.")
        } catch (t: Throwable) {
            Log.w(tag, "connect failed", t)
            emitStatus(false, "Connect failed.", t.message)
            throw t
        }
    }

    /**
     * Publishes the offline retained status as the graceful-DISCONNECT substitute,
     * then issues a normal disconnect. Best-effort: failures during the offline
     * publish are logged but do not block the disconnect.
     */
    suspend fun disconnectGracefully() {
        if (!isConnected) return
        try {
            publishStationStatus(online = false)
        } catch (t: Throwable) {
            Log.w(tag, "could not publish offline status before disconnect", t)
            emitStatus(isConnected, "Could not publish offline status before disconnect.", t.message)
        }
        try {
            // Mute the connectedListener that would otherwise fire after a future
            // reconnect-on-app-relaunch from this same instance (defensive).
            initialConnectComplete = false
            client.disconnect().await()
            emitStatus(false, "Disconnected from broker.")
        } catch (t: Throwable) {
            Log.w(tag, "disconnect failed", t)
            emitStatus(false, "Disconnect failed.", t.message)
        }
    }

    suspend fun publishClaimRequest(requestId: String) {
        if (!isConnected) {
            throw IllegalStateException("Not connected to broker - cannot publish claim request.")
        }

        val payload = ClaimRequestMessage(
            requestId = requestId,
            stationId = config.stationId,
            requestedAt = utcNow()
        )
        publish(
            topic = Topics.stationClaimRequest(config.stationId),
            payload = outboundJson.encodeToString(ClaimRequestMessage.serializer(), payload).encodeToByteArray()
        )
    }

    suspend fun publishClaimEnd(requestId: String, claimId: String) {
        if (!isConnected) {
            throw IllegalStateException("Not connected to broker - cannot publish claim end.")
        }

        val payload = ClaimEndMessage(
            requestId = requestId,
            claimId = claimId,
            stationId = config.stationId,
            endedAt = utcNow()
        )
        publish(
            topic = Topics.stationClaimEnd(config.stationId),
            payload = outboundJson.encodeToString(ClaimEndMessage.serializer(), payload).encodeToByteArray()
        )
    }

    suspend fun publishCreditAck(seqNo: Long) {
        if (!isConnected) {
            throw IllegalStateException("Not connected to broker - cannot publish credit ack.")
        }

        val payload = CreditAckMessage(
            seqNo = seqNo,
            stationId = config.stationId
        )
        publish(
            topic = Topics.stationCreditAck(config.stationId),
            payload = outboundJson.encodeToString(CreditAckMessage.serializer(), payload).encodeToByteArray()
        )
    }

    suspend fun publishSessionClear(requestId: String) {
        if (!isConnected) {
            throw IllegalStateException("Not connected to broker - cannot publish session clear request.")
        }

        val payload = SessionClearRequestMessage(
            requestId = requestId,
            stationId = config.stationId,
            requestedAt = utcNow()
        )
        publish(
            topic = Topics.stationSessionClear(config.stationId),
            payload = outboundJson.encodeToString(SessionClearRequestMessage.serializer(), payload).encodeToByteArray()
        )
    }

    suspend fun publishCommandAck(
        requestId: String,
        result: String,
        detail: String
    ) {
        if (!isConnected) {
            throw IllegalStateException("Not connected to broker - cannot publish command ack.")
        }

        val payload = AdminCommandAckMessage(
            requestId = requestId,
            stationId = config.stationId,
            result = result,
            detail = detail
        )
        publish(
            topic = Topics.stationCommandAck(config.stationId),
            payload = outboundJson.encodeToString(AdminCommandAckMessage.serializer(), payload).encodeToByteArray()
        )
    }

    suspend fun publishAppInventory(apps: List<TabletInstalledAppMessage>) {
        if (!isConnected) {
            throw IllegalStateException("Not connected to broker - cannot publish app inventory.")
        }

        val payload = TabletAppInventoryMessage(
            stationId = config.stationId,
            deviceType = config.deviceType,
            publishedAt = utcNow(),
            apps = apps
        )
        publish(
            topic = Topics.stationAppInventory(config.stationId),
            payload = outboundJson.encodeToString(TabletAppInventoryMessage.serializer(), payload).encodeToByteArray(),
            retain = true
        )
    }

    private suspend fun subscribeAll() {
        // Subscribe to station-scoped control topics at QoS 1. Subsequent slices
        // will append config/rates here.
        subscribeAllAsync().await()
        emitStatus(true, "Subscribed to session + claim/result + credit + config for ${config.stationId}.")
    }

    private fun subscribeAllAsync(): CompletableFuture<Void> {
        val session = client.subscribeWith()
            .topicFilter(Topics.stationSession(config.stationId))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val claimResult = client.subscribeWith()
            .topicFilter(Topics.stationClaimResult(config.stationId))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val credit = client.subscribeWith()
            .topicFilter(Topics.stationCredit(config.stationId))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val rates = client.subscribeWith()
            .topicFilter(Topics.ConfigRates)
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val tabletApps = client.subscribeWith()
            .topicFilter(Topics.ConfigTabletApps)
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val tabletAdmin = client.subscribeWith()
            .topicFilter(Topics.ConfigTabletAdmin)
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val abusePrevention = client.subscribeWith()
            .topicFilter(Topics.ConfigAbusePrevention)
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val adminOpen = client.subscribeWith()
            .topicFilter(Topics.stationAdminOpen(config.stationId))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val adminCommand = client.subscribeWith()
            .topicFilter(Topics.stationCommand(config.stationId))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        val appsReset = client.subscribeWith()
            .topicFilter(Topics.stationAppsReset(config.stationId))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        // S50b: retained OTA manifest. Without this subscribe, the dispatch
        // handler for ConfigUpdate below never fires and the Update badge
        // stays hidden forever.
        val updateManifest = client.subscribeWith()
            .topicFilter(Topics.ConfigUpdate)
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
        return CompletableFuture.allOf(session, claimResult, credit, rates, tabletApps, tabletAdmin, abusePrevention, adminOpen, adminCommand, updateManifest, appsReset)
    }

    // Called on every successful connect, including the implicit one inside
    // the initial connect() coroutine. The first call is suppressed because
    // connect() already awaits the explicit retained online publish.
    private fun onConnectedReconnect() {
        if (!initialConnectComplete) {
            return
        }
        // Refresh subscriptions first. cleanSession=false should preserve them
        // across WiFi blips, but a broker restart may lose the persistent
        // session. Re-subscribing is idempotent and covers both failure modes.
        subscribeAllAsync()
            .thenCompose { publishStationStatusAsync(online = true) }
            .whenComplete { _, error ->
                if (error != null) {
                    Log.w(tag, "reconnect resubscribe/status refresh failed", error)
                    emitStatus(false, "Reconnect setup failed.", error.message)
                } else {
                    emitStatus(true, "Reconnected - refreshed session subscribe and online status.")
                }
            }
    }

    private fun dispatchIncoming(topic: String, payload: ByteArray) {
        when (topic) {
            Topics.stationSession(config.stationId) -> dispatch<SessionMessage>(payload, "session") {
                onSessionReceived?.invoke(it)
            }
            Topics.stationClaimResult(config.stationId) -> dispatch<ClaimResultMessage>(payload, "claim/result") {
                onClaimResultReceived?.invoke(it)
            }
            Topics.stationCredit(config.stationId) -> dispatch<CreditMessage>(payload, "credit") {
                onCreditReceived?.invoke(it)
            }
            Topics.ConfigRates -> dispatch<RateConfigMessage>(payload, "rates") {
                onRateConfigReceived?.invoke(it)
            }
            Topics.ConfigTabletApps -> dispatch<TabletAppConfigMessage>(payload, "tablet-apps") {
                onTabletAppConfigReceived?.invoke(it)
            }
            Topics.ConfigTabletAdmin -> dispatch<TabletAdminConfigMessage>(payload, "tablet-admin") {
                onTabletAdminConfigReceived?.invoke(it)
            }
            Topics.ConfigAbusePrevention -> dispatch<AbusePreventionConfigMessage>(payload, "abuse-prevention") {
                onAbusePreventionConfigReceived?.invoke(it)
            }
            Topics.ConfigUpdate -> dispatch<UpdateManifest>(payload, "update-manifest") {
                onUpdateManifestReceived?.invoke(it)
            }
            Topics.stationAdminOpen(config.stationId) -> {
                Log.i(tag, "admin-open command received from server")
                onAdminOpenReceived?.invoke()
            }
            Topics.stationCommand(config.stationId) -> dispatch<AdminCommandMessage>(payload, "admin-command") {
                onAdminCommandReceived?.invoke(it)
            }
            Topics.stationAppsReset(config.stationId) -> dispatch<AppResetCommandMessage>(payload, "apps/reset") {
                onAppResetReceived?.invoke(it)
            }
        }
    }

    private inline fun <reified T> dispatch(
        payload: ByteArray,
        topicLabel: String,
        emit: (T) -> Unit
    ) {
        try {
            val text = payload.decodeToString()
            val message = inboundJson.decodeFromString<T>(text)
            emit(message)
        } catch (t: Throwable) {
            Log.w(tag, "$topicLabel message parse failed", t)
            emitStatus(isConnected, "$topicLabel message parse failed.", t.message)
        }
    }

    private suspend fun publishStationStatus(online: Boolean) {
        publishStationStatusAsync(online)
            .await()
    }

    private fun publishStationStatusAsync(online: Boolean): CompletableFuture<Mqtt3Publish> {
        val payload = serializeStatus(online = online, includeBootFields = online)
        return client.publishWith()
            .topic(Topics.stationStatus(config.stationId))
            .payload(payload)
            .qos(MqttQos.AT_LEAST_ONCE)
            .retain(true)
            .send()
    }

    private suspend fun publish(
        topic: String,
        payload: ByteArray,
        retain: Boolean = false
    ) {
        client.publishWith()
            .topic(topic)
            .payload(payload)
            .qos(MqttQos.AT_LEAST_ONCE)
            .retain(retain)
            .send()
            .await()
    }

    private fun serializeStatus(online: Boolean, includeBootFields: Boolean): ByteArray {
        val message = StationStatusMessage(
            online = online,
            deviceId = config.stationId,
            deviceType = if (includeBootFields) config.deviceType else null,
            appVersion = if (includeBootFields) config.appVersion else null,
            uptimeSec = if (includeBootFields) getUptimeSeconds() else null
        )
        return outboundJson.encodeToString(StationStatusMessage.serializer(), message).encodeToByteArray()
    }

    private fun getUptimeSeconds(): Long {
        if (bootElapsedMs == 0L) return 0
        return (SystemClock.elapsedRealtime() - bootElapsedMs) / 1000
    }

    private fun utcNow(): String =
        utcFormatter.format(Instant.now())

    private fun emitStatus(connected: Boolean, message: String, detail: String? = null) {
        onStatusChange?.invoke(MqttConnectionStatus(connected, message, detail))
    }
}

data class MqttConnectionStatus(
    val connected: Boolean,
    val message: String,
    val detail: String? = null
)
