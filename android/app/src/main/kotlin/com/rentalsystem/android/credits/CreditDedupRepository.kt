package com.rentalsystem.android.credits

interface CreditDedupRepository {
    fun getLastProcessedSeqNo(stationId: String): Long
    fun markProcessedIfNewer(stationId: String, seqNo: Long): Boolean

    /**
     * Force the local dedup pointer to a server-authoritative value. Used by
     * the session-tracker realignment path: if the retained session arrives
     * with lastCreditSeqNo < our local pointer, the server's SQLite has been
     * reset (or the station re-registered with a fresh credit counter) and
     * keeping our higher pointer would silently drop every future credit.
     *
     * Returns true if the pointer was actually changed, false if the local
     * value was already <= the authoritative value (no-op).
     */
    fun realignTo(stationId: String, authoritativeSeqNo: Long): Boolean
}
