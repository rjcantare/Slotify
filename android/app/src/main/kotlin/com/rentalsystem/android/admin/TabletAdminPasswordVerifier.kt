package com.rentalsystem.android.admin

import com.rentalsystem.android.mqtt.TabletAdminConfigMessage
import java.security.MessageDigest
import java.util.Base64

object TabletAdminPasswordVerifier {
    private const val Algorithm = "sha256-salt-prefix-v1"

    fun isValid(
        entered: String,
        config: TabletAdminConfigMessage?,
        legacyPassword: String
    ): Boolean {
        if (config == null) {
            return entered == legacyPassword
        }

        if (config.algorithm != Algorithm ||
            config.passwordSalt.isBlank() ||
            config.passwordHash.isBlank()) {
            return false
        }

        return try {
            val salt = Base64.getDecoder().decode(config.passwordSalt)
            val expected = Base64.getDecoder().decode(config.passwordHash)
            val enteredBytes = entered.toByteArray(Charsets.UTF_8)
            val input = ByteArray(salt.size + enteredBytes.size)
            System.arraycopy(salt, 0, input, 0, salt.size)
            System.arraycopy(enteredBytes, 0, input, salt.size, enteredBytes.size)
            val actual = MessageDigest.getInstance("SHA-256").digest(input)
            MessageDigest.isEqual(actual, expected)
        } catch (_: IllegalArgumentException) {
            false
        }
    }
}
