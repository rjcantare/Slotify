package com.rentalsystem.android.mqtt

import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Test

class AppResetCommandMessageTest {
    private val json = Json { ignoreUnknownKeys = true }

    @Test
    fun parsesWireShape() {
        val msg = json.decodeFromString(
            AppResetCommandMessage.serializer(),
            """{"requestId":"reset-1","stationId":"tab-01","packages":["com.mobile.legends","com.roblox.client"]}"""
        )
        assertEquals("reset-1", msg.requestId)
        assertEquals("tab-01", msg.stationId)
        assertEquals(listOf("com.mobile.legends", "com.roblox.client"), msg.packages)
    }

    @Test
    fun ignoresUnknownFieldsAndDefaultsMissing() {
        val msg = json.decodeFromString(
            AppResetCommandMessage.serializer(),
            """{"requestId":"r","extra":"ignored"}"""
        )
        assertEquals("r", msg.requestId)
        assertEquals(emptyList<String>(), msg.packages)
    }
}
