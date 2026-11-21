package com.rentalsystem.android.rates

import com.rentalsystem.android.mqtt.InsertCoinRateRuleMessage
import com.rentalsystem.android.mqtt.RateConfigMessage

object RateDisplayFormatter {
    private val DefaultInsertCoinRule = InsertCoinRateRuleMessage(
        starterMaxPesos = 7,
        starterSeconds = 1800,
        normalizedFromPesos = 10,
        perPesoSeconds = 360
    )

    fun buildLockedRateText(
        sessionDeviceType: String,
        fallbackDeviceType: String?,
        rateConfig: RateConfigMessage?
    ): String {
        val rule = getInsertCoinRule(sessionDeviceType, fallbackDeviceType, rateConfig)

        val afterStarter = rule.starterMaxPesos + 1
        val beforeNormalized = rule.normalizedFromPesos - 1
        val parts = mutableListOf(
            "${formatPesos(rule.starterMaxPesos)} = ${formatRateDuration(calculateInsertCoinSeconds(rule, rule.starterMaxPesos))}"
        )

        if (afterStarter < rule.normalizedFromPesos) {
            parts += "${formatPesos(afterStarter)} = ${formatRateDuration(calculateInsertCoinSeconds(rule, afterStarter))}"
        }

        if (beforeNormalized > afterStarter) {
            parts += "${formatPesos(beforeNormalized)} = ${formatRateDuration(calculateInsertCoinSeconds(rule, beforeNormalized))}"
        }

        parts += "${formatPesos(rule.normalizedFromPesos)} = ${formatRateDuration(calculateInsertCoinSeconds(rule, rule.normalizedFromPesos))}"
        parts += "${formatPesos(20)} = ${formatRateDuration(calculateInsertCoinSeconds(rule, 20))}"

        return "Rates: drop ${formatPesos(rule.starterMaxPesos)} or more to unlock | ${parts.joinToString(" | ")}"
    }

    fun buildCustomerRateText(
        sessionDeviceType: String,
        fallbackDeviceType: String?,
        rateConfig: RateConfigMessage?,
        hasActiveSession: Boolean
    ): String {
        val deviceType = sessionDeviceType.takeIf { it.isNotBlank() }
            ?: fallbackDeviceType
            ?: "pc"

        return if (hasActiveSession) {
            val rates = tryGetAddTimeRates(deviceType, rateConfig)
            val parts = listOf(5, 10, 20).map { pesos ->
                "${formatPesos(pesos)} = ${formatRateDuration(rates[pesos] ?: defaultAddTimeSeconds(pesos))}"
            }
            "Add time: ${parts.joinToString(" | ")}"
        } else {
            val rule = getInsertCoinRule(deviceType, fallbackDeviceType, rateConfig)
            val parts = listOf(rule.starterMaxPesos, rule.normalizedFromPesos, 20)
                .distinct()
                .map { pesos ->
                    "${formatPesos(pesos)} = ${formatRateDuration(calculateInsertCoinSeconds(rule, pesos))}"
                }
            "Start: ${parts.joinToString(" | ")}"
        }
    }

    fun getInsertCoinRule(
        sessionDeviceType: String,
        fallbackDeviceType: String?,
        rateConfig: RateConfigMessage?
    ): InsertCoinRateRuleMessage {
        val deviceType = sessionDeviceType.takeIf { it.isNotBlank() }
            ?: fallbackDeviceType
            ?: "pc"
        return tryGetInsertCoinRule(deviceType, rateConfig) ?: DefaultInsertCoinRule
    }

    fun buildLockedClaimProgressText(
        sessionDeviceType: String,
        fallbackDeviceType: String?,
        rateConfig: RateConfigMessage?,
        runningTotalPesos: Int
    ): String {
        val rule = getInsertCoinRule(sessionDeviceType, fallbackDeviceType, rateConfig)
        val pesos = runningTotalPesos.coerceAtLeast(0)
        val seconds = calculateInsertCoinSeconds(rule, pesos)
        return if (pesos < rule.starterMaxPesos) {
            val more = rule.starterMaxPesos - pesos
            "PHP $pesos — need PHP $more more"
        } else {
            "PHP $pesos — ${formatClockDuration(seconds)} ready"
        }
    }

    fun calculateInsertCoinSeconds(rule: InsertCoinRateRuleMessage, totalPesos: Int): Int {
        if (totalPesos <= 0) {
            return 0
        }
        if (totalPesos < rule.starterMaxPesos) {
            return 0
        }
        if (totalPesos == rule.starterMaxPesos) {
            return rule.starterSeconds
        }
        if (totalPesos < rule.normalizedFromPesos) {
            return rule.starterSeconds + ((totalPesos - rule.starterMaxPesos) * rule.perPesoSeconds)
        }
        return totalPesos * rule.perPesoSeconds
    }

    private fun tryGetInsertCoinRule(
        deviceType: String,
        rateConfig: RateConfigMessage?
    ): InsertCoinRateRuleMessage? {
        val rules = rateConfig?.insertCoinRules ?: return null
        return rules[deviceType] ?: rules["pc"]
    }

    private fun tryGetAddTimeRates(
        deviceType: String,
        rateConfig: RateConfigMessage?
    ): Map<Int, Int> {
        val ratesByDevice = rateConfig?.addTimeRates?.takeIf { it.isNotEmpty() }
            ?: rateConfig?.rates
            ?: emptyMap()
        val rates = ratesByDevice[deviceType] ?: ratesByDevice["pc"] ?: emptyMap()
        return rates.mapNotNull { (pesoText, seconds) ->
            pesoText.toIntOrNull()?.let { it to seconds }
        }.toMap()
    }

    private fun defaultAddTimeSeconds(pesos: Int): Int = pesos * 360

    private fun formatRateDuration(totalSeconds: Int): String {
        val totalMinutes = totalSeconds / 60
        if (totalMinutes < 60) {
            return "$totalMinutes min"
        }

        val hours = totalMinutes / 60
        val minutes = totalMinutes % 60
        val hourLabel = if (hours == 1) "hr" else "hrs"
        return if (minutes == 0) {
            "$hours $hourLabel"
        } else {
            "$hours $hourLabel $minutes min"
        }
    }

    private fun formatClockDuration(totalSeconds: Int): String {
        val safe = totalSeconds.coerceAtLeast(0)
        val hours = safe / 3600
        val minutes = (safe % 3600) / 60
        val seconds = safe % 60
        return "%02d:%02d:%02d".format(hours, minutes, seconds)
    }

    private fun formatPesos(pesos: Int): String =
        "PHP $pesos"
}
