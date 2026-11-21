package com.rentalsystem.android.mqtt

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Test

class AdminCommandMessageTest {
    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun parsesCustomerLogoutCommand() {
        val msg = json.decodeFromString(
            AdminCommandMessage.serializer(),
            """{"requestId":"cmd-1","command":"customer_logout","issuedAt":"2026-06-01T00:00:00Z","stationId":"tab-01","reason":"operator customer logout"}"""
        )

        assertEquals("cmd-1", msg.requestId)
        assertEquals("customer_logout", msg.command)
        assertEquals("tab-01", msg.stationId)
        assertEquals("operator customer logout", msg.reason)
    }

    @Test
    fun serializesCommandAckWireShape() {
        val text = json.encodeToString(
            AdminCommandAckMessage.serializer(),
            AdminCommandAckMessage(
                requestId = "cmd-1",
                stationId = "tab-01",
                result = "acknowledged",
                detail = "customer user logout requested"
            )
        )

        assertEquals(
            """{"requestId":"cmd-1","stationId":"tab-01","result":"acknowledged","detail":"customer user logout requested"}""",
            text
        )
    }
}
