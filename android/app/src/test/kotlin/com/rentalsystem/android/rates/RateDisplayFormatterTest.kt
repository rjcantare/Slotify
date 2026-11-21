package com.rentalsystem.android.rates

import com.rentalsystem.android.mqtt.InsertCoinRateRuleMessage
import com.rentalsystem.android.mqtt.RateConfigMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RateDisplayFormatterTest {
    @Test
    fun calculatesLockedInsertCoinSeconds() {
        val rule = InsertCoinRateRuleMessage(
            starterMaxPesos = 7,
            starterSeconds = 1800,
            normalizedFromPesos = 10,
            perPesoSeconds = 360
        )

        assertEquals(0, RateDisplayFormatter.calculateInsertCoinSeconds(rule, 6))
        assertEquals(1800, RateDisplayFormatter.calculateInsertCoinSeconds(rule, 7))
        assertEquals(2160, RateDisplayFormatter.calculateInsertCoinSeconds(rule, 8))
        assertEquals(3600, RateDisplayFormatter.calculateInsertCoinSeconds(rule, 10))
        assertEquals(7200, RateDisplayFormatter.calculateInsertCoinSeconds(rule, 20))
    }

    @Test
    fun buildsDeviceSpecificLockedRateTextWithPcFallback() {
        val config = RateConfigMessage(
            insertCoinRules = mapOf(
                "pc" to InsertCoinRateRuleMessage(7, 1800, 10, 360),
                "tab" to InsertCoinRateRuleMessage(8, 2400, 12, 300)
            )
        )

        val tabText = RateDisplayFormatter.buildLockedRateText("tab", "pc", config)
        val phoneText = RateDisplayFormatter.buildLockedRateText("phn", "pc", config)

        assertTrue(tabText.contains("drop PHP 8 or more"))
        assertTrue(tabText.contains("PHP 20 = 1 hr 40 min"))
        assertTrue(phoneText.contains("drop PHP 7 or more"))
        assertTrue(phoneText.contains("PHP 20 = 2 hrs"))
    }

    @Test
    fun buildsCustomerRateTextForLockedAndActiveSessions() {
        val config = RateConfigMessage(
            addTimeRates = mapOf(
                "tab" to mapOf("5" to 1800, "10" to 3600, "20" to 7200)
            ),
            insertCoinRules = mapOf(
                "tab" to InsertCoinRateRuleMessage(7, 1800, 10, 360)
            )
        )

        val lockedText = RateDisplayFormatter.buildCustomerRateText("tab", "pc", config, false)
        val activeText = RateDisplayFormatter.buildCustomerRateText("tab", "pc", config, true)

        assertEquals("Start: PHP 7 = 30 min | PHP 10 = 1 hr | PHP 20 = 2 hrs", lockedText)
        assertEquals("Add time: PHP 5 = 30 min | PHP 10 = 1 hr | PHP 20 = 2 hrs", activeText)
    }

    @Test
    fun buildsLockedClaimStarterWarningAndReadyCopy() {
        val config = RateConfigMessage(
            insertCoinRules = mapOf(
                "tab" to InsertCoinRateRuleMessage(7, 1800, 10, 360)
            )
        )

        val belowStarter = RateDisplayFormatter.buildLockedClaimProgressText("tab", "pc", config, 5)
        val ready = RateDisplayFormatter.buildLockedClaimProgressText("tab", "pc", config, 10)

        // 2026-05-28 session: simplified for kids — short "PHP X — need PHP Y more"
        // when below the starter minimum, "PHP X — HH:MM:SS ready" once at/above it.
        assertEquals("PHP 5 — need PHP 2 more", belowStarter)
        assertEquals("PHP 10 — 01:00:00 ready", ready)
    }
}
