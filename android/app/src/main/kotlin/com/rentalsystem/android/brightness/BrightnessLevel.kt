package com.rentalsystem.android.brightness

object BrightnessLevel {
    fun clampPercent(percent: Int, minPercent: Int, maxPercent: Int): Int =
        percent.coerceIn(minPercent, maxPercent)

    fun stepPercent(
        currentPercent: Int,
        direction: Int,
        minPercent: Int,
        maxPercent: Int,
        stepPercent: Int
    ): Int {
        val safeStep = stepPercent.coerceAtLeast(1)
        val delta = if (direction >= 0) safeStep else -safeStep
        return clampPercent(currentPercent + delta, minPercent, maxPercent)
    }

    fun toWindowBrightness(percent: Int, minPercent: Int, maxPercent: Int): Float =
        clampPercent(percent, minPercent, maxPercent) / 100f

    fun label(percent: Int, minPercent: Int, maxPercent: Int): String =
        "${clampPercent(percent, minPercent, maxPercent)}%"
}
