package com.rentalsystem.android.admin

import com.rentalsystem.android.mqtt.TabletAdminConfigMessage
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TabletAdminPasswordVerifierTest {
    @Test
    fun acceptsServerPublishedSaltedHash() {
        val config = TabletAdminConfigMessage(
            version = 2,
            updatedAt = "2026-05-26T00:00:00.000Z",
            algorithm = "sha256-salt-prefix-v1",
            passwordSalt = "c2FsdA==",
            passwordHash = "C1URbnEb1tFyv0H1CmcBJPsLJjgAQZhOVNCP9kU8Kzk="
        )

        assertTrue(TabletAdminPasswordVerifier.isValid("newpass", config, "admin@123!"))
        assertFalse(TabletAdminPasswordVerifier.isValid("admin@123!", config, "admin@123!"))
    }

    @Test
    fun allowsLegacyPasswordOnlyBeforeConfigArrives() {
        assertTrue(TabletAdminPasswordVerifier.isValid("admin@123!", config = null, "admin@123!"))
        assertFalse(TabletAdminPasswordVerifier.isValid("wrong", config = null, "admin@123!"))
    }

    @Test
    fun rejectsUnsupportedOrMalformedConfig() {
        val wrongAlgorithm = TabletAdminConfigMessage(
            version = 1,
            algorithm = "plain",
            passwordSalt = "c2FsdA==",
            passwordHash = "C1URbnEb1tFyv0H1CmcBJPsLJjgAQZhOVNCP9kU8Kzk="
        )
        val badBase64 = TabletAdminConfigMessage(
            version = 1,
            algorithm = "sha256-salt-prefix-v1",
            passwordSalt = "!",
            passwordHash = "!"
        )

        assertFalse(TabletAdminPasswordVerifier.isValid("newpass", wrongAlgorithm, "admin@123!"))
        assertFalse(TabletAdminPasswordVerifier.isValid("newpass", badBase64, "admin@123!"))
    }
}
