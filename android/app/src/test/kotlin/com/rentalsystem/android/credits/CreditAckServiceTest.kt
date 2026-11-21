package com.rentalsystem.android.credits

import com.rentalsystem.android.mqtt.CreditMessage
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Test

class CreditAckServiceTest {
    @Test
    fun acksDuplicatesButEmitsOnlyNewCredits() = runBlocking {
        val store = FakeCreditDedupRepository()
        val service = CreditAckService("tab-01", store)
        val acks = mutableListOf<Long>()
        val applied = mutableListOf<Long>()
        service.onNewCreditApplied = { applied += it.seqNo }

        service.process(CreditMessage(seqNo = 7, secondsToAdd = 60), publishAck = { acks += it })
        service.process(CreditMessage(seqNo = 7, secondsToAdd = 60), publishAck = { acks += it })

        assertEquals(listOf(7L, 7L), acks)
        assertEquals(listOf(7L), applied)
        assertEquals(7, service.getLastProcessedSeqNo())
    }

    @Test
    fun staleCreditIsAckedAndIgnored() = runBlocking {
        val store = FakeCreditDedupRepository(initial = 9)
        val service = CreditAckService("tab-01", store)
        val acks = mutableListOf<Long>()
        var appliedCount = 0
        service.onNewCreditApplied = { appliedCount++ }

        service.process(CreditMessage(seqNo = 8, secondsToAdd = 60), publishAck = { acks += it })

        assertEquals(listOf(8L), acks)
        assertEquals(0, appliedCount)
        assertEquals(9, service.getLastProcessedSeqNo())
    }

    @Test
    fun realignFromSessionDropsPointerWhenServerSeqnoIsLower() = runBlocking {
        // S28f: simulate a server SQLite reset — local pointer at 80, session
        // says lastCreditSeqNo=3. Without realignment, every future credit
        // (seqno 4, 5, 6...) would be silently dropped.
        val store = FakeCreditDedupRepository(initial = 80)
        val service = CreditAckService("tab-01", store)

        val realigned = service.realignFromSession(3)

        assertEquals(true, realigned)
        assertEquals(3, service.getLastProcessedSeqNo())

        // After realignment, a fresh credit at seqNo=4 is accepted.
        var appliedCount = 0
        service.onNewCreditApplied = { appliedCount++ }
        service.process(CreditMessage(seqNo = 4, secondsToAdd = 60), publishAck = {})
        assertEquals(1, appliedCount)
        assertEquals(4, service.getLastProcessedSeqNo())
    }

    @Test
    fun realignFromSessionIsNoOpWhenServerSeqnoMatchesOrLeads() = runBlocking {
        val store = FakeCreditDedupRepository(initial = 7)
        val service = CreditAckService("tab-01", store)

        // Equal: no-op (no need to move).
        assertEquals(false, service.realignFromSession(7))
        assertEquals(7, service.getLastProcessedSeqNo())

        // Higher: also no-op — markProcessedIfNewer is the path that advances
        // the pointer forward on real credit messages. Realignment is a
        // backwards-only safety net.
        assertEquals(false, service.realignFromSession(12))
        assertEquals(7, service.getLastProcessedSeqNo())
    }

    private class FakeCreditDedupRepository(initial: Long = 0) : CreditDedupRepository {
        private var last = initial

        override fun getLastProcessedSeqNo(stationId: String): Long =
            last

        override fun markProcessedIfNewer(stationId: String, seqNo: Long): Boolean {
            if (seqNo <= last) {
                return false
            }
            last = seqNo
            return true
        }

        override fun realignTo(stationId: String, authoritativeSeqNo: Long): Boolean {
            if (authoritativeSeqNo >= last) {
                return false
            }
            last = authoritativeSeqNo
            return true
        }
    }
}
