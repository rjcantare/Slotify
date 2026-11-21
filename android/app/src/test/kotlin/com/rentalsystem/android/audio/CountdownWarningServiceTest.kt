package com.rentalsystem.android.audio

import com.rentalsystem.android.config.CountdownWarningConfig
import com.rentalsystem.android.sessions.SessionTrackerState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertNotNull
import org.junit.Test

class CountdownWarningServiceTest {
    @Test
    fun playsFirstWarningOnceWhenCrossingThreshold() {
        val player = FakeWarningSoundPlayer()
        val service = createService(player)

        assertNull(service.update(active(61)))
        assertCue(service.update(active(60)), CountdownWarningCueKind.FirstWarning, 60)
        assertNull(service.update(active(60)))
        assertNull(service.update(active(59)))

        assertEquals(1, player.cues.size)
    }

    @Test
    fun playsFinalCountdownOncePerSecond() {
        val player = FakeWarningSoundPlayer()
        val service = createService(player)

        assertCue(service.update(active(60)), CountdownWarningCueKind.FirstWarning, 60)
        assertNull(service.update(active(11)))
        assertCue(service.update(active(10)), CountdownWarningCueKind.FinalCountdown, 10)
        assertNull(service.update(active(10)))
        assertCue(service.update(active(9)), CountdownWarningCueKind.FinalCountdown, 9)
        assertCue(service.update(active(1)), CountdownWarningCueKind.FinalCountdown, 1)
        assertNull(service.update(active(0)))

        assertEquals(listOf(60L, 10L, 9L, 1L), player.cues.map { it.remainingSec })
    }

    @Test
    fun creditExtensionAboveThresholdResetsWarningState() {
        val player = FakeWarningSoundPlayer()
        val service = createService(player)

        assertCue(service.update(active(60)), CountdownWarningCueKind.FirstWarning, 60)
        assertCue(service.update(active(10)), CountdownWarningCueKind.FinalCountdown, 10)

        assertNull(service.update(active(90)))
        assertCue(service.update(active(60)), CountdownWarningCueKind.FirstWarning, 60)
        assertCue(service.update(active(10)), CountdownWarningCueKind.FinalCountdown, 10)

        assertEquals(
            listOf(
                CountdownWarningCueKind.FirstWarning,
                CountdownWarningCueKind.FinalCountdown,
                CountdownWarningCueKind.FirstWarning,
                CountdownWarningCueKind.FinalCountdown
            ),
            player.cues.map { it.kind }
        )
    }

    @Test
    fun lockedOrMissingSessionSuppressesAndResetsWarnings() {
        val player = FakeWarningSoundPlayer()
        val service = createService(player)

        assertCue(service.update(active(10)), CountdownWarningCueKind.FinalCountdown, 10)
        assertNull(service.update(locked(0)))
        assertNull(service.update(SessionTrackerState.NoSession))
        assertCue(service.update(active(10)), CountdownWarningCueKind.FinalCountdown, 10)

        assertEquals(listOf(10L, 10L), player.cues.map { it.remainingSec })
    }

    @Test
    fun newSessionResetsWarningState() {
        val player = FakeWarningSoundPlayer()
        val service = createService(player)

        assertCue(service.update(active(60, "s-1")), CountdownWarningCueKind.FirstWarning, 60)
        assertNull(service.update(active(59, "s-1")))
        assertCue(service.update(active(60, "s-2")), CountdownWarningCueKind.FirstWarning, 60)

        assertEquals(2, player.cues.size)
    }

    @Test
    fun playerFailureDoesNotSurface() {
        val service = CountdownWarningService(
            CountdownWarningConfig(enabled = true, firstWarningSec = 60, finalCountdownStartSec = 10),
            ThrowingWarningSoundPlayer()
        )

        assertCue(service.update(active(60)), CountdownWarningCueKind.FirstWarning, 60)
    }

    private fun createService(player: FakeWarningSoundPlayer): CountdownWarningService =
        CountdownWarningService(
            CountdownWarningConfig(enabled = true, firstWarningSec = 60, finalCountdownStartSec = 10),
            player
        )

    private fun active(remainingSec: Long, sessionId: String = "s-1"): SessionTrackerState =
        SessionTrackerState(
            hasSession = true,
            sessionId = sessionId,
            stationId = "tab-01",
            deviceType = "tab",
            locked = false,
            effectiveRemainingSec = remainingSec,
            lastCreditSeqNo = 1,
            serverUpdatedAt = "2026-05-20T00:00:00.000Z"
        )

    private fun locked(remainingSec: Long, sessionId: String = "s-1"): SessionTrackerState =
        active(remainingSec, sessionId).copy(locked = true)

    private fun assertCue(
        cue: CountdownWarningCue?,
        kind: CountdownWarningCueKind,
        remainingSec: Long
    ) {
        assertNotNull(cue)
        assertEquals(kind, cue!!.kind)
        assertEquals(remainingSec, cue.remainingSec)
    }

    private class FakeWarningSoundPlayer : CountdownWarningSoundPlayer {
        val cues = mutableListOf<CountdownWarningCue>()

        override fun play(cue: CountdownWarningCue) {
            cues.add(cue)
        }
    }

    private class ThrowingWarningSoundPlayer : CountdownWarningSoundPlayer {
        override fun play(cue: CountdownWarningCue) {
            throw IllegalStateException("audio unavailable")
        }
    }
}
