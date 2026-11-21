package com.rentalsystem.android.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Resolves the MQTT broker via mDNS / DNS-SD (NsdManager) when the config has
 * broker.host="auto". Unlike [UdpDiscoveryListener], which waits for the
 * server's limited UDP broadcast, this uses multicast — which Wi-Fi APs
 * forward — so it works on Wi-Fi tablets/phones. The UDP broadcast does NOT
 * reach Wi-Fi clients (the production dry-run finding), which is why this app
 * uses mDNS for auto-discovery.
 *
 * Mirrors the service contract advertised by the server's
 * MdnsDiscoveryAdvertiser (RentalShared.Discovery.MdnsDiscovery): the DNS-SD
 * service type is "_slotify-mqtt._tcp". Guardrail: this resolves only the
 * broker host/port — the MQTT topics/QoS/retain/ACK contract is unchanged.
 *
 * NsdManager is asynchronous and delivers its listener callbacks on its own
 * internal thread (not the app main thread), so [discover] blocks the calling
 * thread on a queue the callbacks feed. Call it from a background thread.
 */
object MdnsDiscoveryListener {

    private const val tag = "MdnsDiscovery"
    private const val SERVICE_TYPE = "_slotify-mqtt._tcp"
    private const val DEFAULT_TIMEOUT_MS = 10_000L

    data class DiscoveryResult(val brokerHost: String, val brokerPort: Int)

    /**
     * Browses for the Slotify broker service and resolves the first match to a
     * host/port. Blocks the calling thread up to [timeoutMs]; returns null on
     * timeout or error. Call from a background thread.
     */
    // resolveService(...) and NsdServiceInfo.getHost() are deprecated in API 34
    // (replaced by registerServiceInfoCallback / getHostAddresses), but those
    // require API 34+ and minSdk is 26. The deprecated calls still function on
    // API 26-36; revisit if a future minSdk drops them.
    @Suppress("DEPRECATION")
    fun discover(context: Context, timeoutMs: Long = DEFAULT_TIMEOUT_MS): DiscoveryResult? {
        val nsd = context.getSystemService(Context.NSD_SERVICE) as? NsdManager
        if (nsd == null) {
            Log.w(tag, "NsdManager unavailable")
            return null
        }

        val results = ArrayBlockingQueue<DiscoveryResult>(1)
        // Gate so only the first found instance is resolved at a time
        // (concurrent resolves fail with FAILURE_ALREADY_ACTIVE on older APIs).
        val resolving = AtomicBoolean(false)

        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.w(tag, "resolve failed (code=$errorCode); will retry on next find")
                resolving.set(false)
            }

            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                val host = serviceInfo.host?.hostAddress
                val port = serviceInfo.port
                if (host != null && port > 0) {
                    Log.i(tag, "resolved broker -> $host:$port (${serviceInfo.serviceName})")
                    results.offer(DiscoveryResult(host, port))
                } else {
                    Log.w(tag, "resolved service missing host/port")
                    resolving.set(false)
                }
            }
        }

        val discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(tag, "start discovery failed (code=$errorCode)")
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onDiscoveryStarted(serviceType: String) {
                Log.i(tag, "discovery started for $serviceType")
            }
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {}
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                // The reported serviceType may carry a trailing dot or subtype;
                // match loosely on the service label.
                if (serviceInfo.serviceType.contains("slotify-mqtt") &&
                    resolving.compareAndSet(false, true)
                ) {
                    nsd.resolveService(serviceInfo, resolveListener)
                }
            }
        }

        return try {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
            results.poll(timeoutMs, TimeUnit.MILLISECONDS)
        } catch (t: Throwable) {
            Log.w(tag, "mDNS discovery error", t)
            null
        } finally {
            // stopServiceDiscovery throws if discovery never started; ignore.
            try {
                nsd.stopServiceDiscovery(discoveryListener)
            } catch (t: Throwable) {
                Log.w(tag, "stopServiceDiscovery error: ${t.message}")
            }
        }
    }
}
