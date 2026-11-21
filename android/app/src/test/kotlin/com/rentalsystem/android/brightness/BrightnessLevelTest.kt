package com.rentalsystem.android.brightness

import com.rentalsystem.android.config.BrightnessConfig
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class BrightnessLevelTest {
    @Test
    fun stepPercent_clamps_at_bounds() {
        assertEquals(100, BrightnessLevel.stepPercent(95, 1, 20, 100, 10))
        assertEquals(20, BrightnessLevel.stepPercent(25, -1, 20, 100, 10))
    }

    @Test
    fun toWindowBrightness_returnsZeroToOneFloat() {
        assertEquals(0.2f, BrightnessLevel.toWindowBrightness(10, 20, 100), 0.001f)
        assertEquals(0.75f, BrightnessLevel.toWindowBrightness(75, 20, 100), 0.001f)
        assertEquals(1.0f, BrightnessLevel.toWindowBrightness(120, 20, 100), 0.001f)
    }

    @Test
    fun label_usesClampedPercent() {
        assertEquals("20%", BrightnessLevel.label(5, 20, 100))
        assertEquals("80%", BrightnessLevel.label(80, 20, 100))
    }

    @Test
    fun brightnessConfig_rejectsInvalidRanges() {
        assertThrows(IllegalArgumentException::class.java) {
            BrightnessConfig(defaultPercent = 10, minPercent = 20, maxPercent = 100).validate()
        }
        assertThrows(IllegalArgumentException::class.java) {
            BrightnessConfig(minPercent = 80, maxPercent = 70).validate()
        }
        assertThrows(IllegalArgumentException::class.java) {
            BrightnessConfig(minPercent = 20, maxPercent = 100, stepPercent = 0).validate()
        }
    }
}
