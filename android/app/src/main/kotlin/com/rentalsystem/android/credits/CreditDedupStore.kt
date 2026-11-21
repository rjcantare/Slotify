package com.rentalsystem.android.credits

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

class CreditDedupStore(context: Context) : SQLiteOpenHelper(
    context,
    DatabaseName,
    null,
    DatabaseVersion
), CreditDedupRepository {
    private val gate = Any()

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE credit_dedup (
                station_id TEXT PRIMARY KEY NOT NULL,
                last_processed_seq_no INTEGER NOT NULL
            )
            """.trimIndent()
        )
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // Version 1 is the first local persistence slice.
    }

    override fun getLastProcessedSeqNo(stationId: String): Long =
        synchronized(gate) {
            readableDatabase.rawQuery(
                "SELECT last_processed_seq_no FROM credit_dedup WHERE station_id = ?",
                arrayOf(stationId)
            ).use { cursor ->
                if (cursor.moveToFirst()) cursor.getLong(0) else 0L
            }
        }

    override fun markProcessedIfNewer(stationId: String, seqNo: Long): Boolean =
        synchronized(gate) {
            val db = writableDatabase
            db.beginTransaction()
            try {
                val current = db.rawQuery(
                    "SELECT last_processed_seq_no FROM credit_dedup WHERE station_id = ?",
                    arrayOf(stationId)
                ).use { cursor ->
                    if (cursor.moveToFirst()) cursor.getLong(0) else 0L
                }

                if (seqNo <= current) {
                    db.setTransactionSuccessful()
                    return@synchronized false
                }

                db.execSQL(
                    """
                    INSERT OR REPLACE INTO credit_dedup (station_id, last_processed_seq_no)
                    VALUES (?, ?)
                    """.trimIndent(),
                    arrayOf<Any>(stationId, seqNo)
                )
                db.setTransactionSuccessful()
                true
            } finally {
                db.endTransaction()
            }
        }

    override fun realignTo(stationId: String, authoritativeSeqNo: Long): Boolean =
        synchronized(gate) {
            val db = writableDatabase
            db.beginTransaction()
            try {
                val current = db.rawQuery(
                    "SELECT last_processed_seq_no FROM credit_dedup WHERE station_id = ?",
                    arrayOf(stationId)
                ).use { cursor ->
                    if (cursor.moveToFirst()) cursor.getLong(0) else 0L
                }

                if (authoritativeSeqNo >= current) {
                    // Local is already at or behind the authoritative value
                    // (normal steady-state). markProcessedIfNewer handles the
                    // forward direction; this path only fires when we've
                    // detected a server reset.
                    db.setTransactionSuccessful()
                    return@synchronized false
                }

                db.execSQL(
                    """
                    INSERT OR REPLACE INTO credit_dedup (station_id, last_processed_seq_no)
                    VALUES (?, ?)
                    """.trimIndent(),
                    arrayOf<Any>(stationId, authoritativeSeqNo)
                )
                db.setTransactionSuccessful()
                true
            } finally {
                db.endTransaction()
            }
        }

    companion object {
        private const val DatabaseName = "rental_android.db"
        private const val DatabaseVersion = 1
    }
}
