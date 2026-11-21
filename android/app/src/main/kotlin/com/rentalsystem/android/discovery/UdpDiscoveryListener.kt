package com.rentalsystem.android.discovery

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketTimeoutException

/**
 * Listens for the server's UDP discovery broadcast on port 1884.
 * Called once at startup when the config has broker.host="auto".
 * Returns the discovered broker host and port, or null if no
 * broadcast is received within the timeout window.
 */
object UdpDiscoveryListener {

    private const val PORT = 1884
    private const val TIMEOUT_MS = 12_000
    private const val MAX_PACKET_BYTES = 512

    private val json = Json { ignoreUnknownKeys = true }

    data class DiscoveryResult(val brokerHost: String, val brokerPort: Int)

    /**
     * Waits for a discovery broadcast on UDP port 1884.
     * Blocks the calling thread for up to TIMEOUT_MS. Returns the
     * broker endpoint or null on timeout/error. Call from a background
     * thread or during init where a short block is acceptable.
     */
    fun discover(): DiscoveryResult? {
        val socket = DatagramSocket(null)
        socket.reuseAddress = true
        socket.soTimeout = TIMEOUT_MS
        socket.bind(InetSocketAddress(PORT))

        val buffer = ByteArray(MAX_PACKET_BYTES)
        val packet = DatagramPacket(buffer, buffer.size)

        return try {
            socket.receive(packet)
            val raw = String(packet.data, 0, packet.length, Charsets.UTF_8)
            val element = json.parseToJsonElement(raw) as? JsonObject ?: kotlin.run {
                socket.close()
                return null
            }

            val type = stringField(element, "type", "Type")
            if (type != "rental-discovery") {
                socket.close()
                return null
            }

            val host = stringField(element, "brokerHost", "BrokerHost") ?: kotlin.run {
                socket.close()
                return null
            }

            // brokerPort is a JSON number — JsonPrimitive content will be the
            // string representation, e.g. "1883"
            val port = stringField(element, "brokerPort", "BrokerPort")?.toIntOrNull() ?: 1883

            socket.close()
            DiscoveryResult(host, port)
        } catch (_: SocketTimeoutException) {
            socket.close()
            null
        } catch (_: Exception) {
            socket.close()
            null
        }
    }

    private fun stringField(element: JsonObject, vararg names: String): String? {
        for (name in names) {
            val value = element[name]?.jsonPrimitive?.content
            if (!value.isNullOrBlank()) {
                return value
            }
        }
        return null
    }
}
