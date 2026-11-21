package com.rentalsystem.android.credits

import com.rentalsystem.android.mqtt.CreditMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class CreditAckService(
    private val stationId: String,
    private val store: CreditDedupRepository
) {
    @Volatile
    var onNewCreditApplied: ((CreditMessage) -> Unit)? = null

    suspend fun process(
        message: CreditMessage,
        publishAck: suspend (Long) -> Unit
    ) {
        // ACK first, even for duplicates, so the server's retry path can stop.
        publishAck(message.seqNo)

        val isNew = withContext(Dispatchers.IO) {
            store.markProcessedIfNewer(stationId, message.seqNo)
        }
        if (isNew) {
            onNewCreditApplied?.invoke(message)
        }
    }

    suspend fun getLastProcessedSeqNo(): Long =
        withContext(Dispatchers.IO) {
            store.getLastProcessedSeqNo(stationId)
        }

    /**
     * Drop the local dedup pointer down to the retained session's authoritative
     * lastCreditSeqNo when the latter is lower. Detects server SQLite reset
     * (dev wipe, station re-registration) — without this, every credit
     * post-reset would be silently dropped by the stale local pointer (root
     * cause hypothesis #4 from known-issues.md). No-op in steady state.
     *
     * Returns true if the pointer was actually moved.
     */
    suspend fun realignFromSession(sessionLastCreditSeqNo: Long): Boolean =
        withContext(Dispatchers.IO) {
            store.realignTo(stationId, sessionLastCreditSeqNo)
        }
}
