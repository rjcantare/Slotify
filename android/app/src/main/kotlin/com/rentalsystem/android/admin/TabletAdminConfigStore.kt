package com.rentalsystem.android.admin

import android.content.Context
import com.rentalsystem.android.mqtt.TabletAdminConfigMessage

class TabletAdminConfigStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PreferencesName,
        Context.MODE_PRIVATE
    )

    fun load(): TabletAdminConfigMessage? {
        val version = preferences.getInt(KeyVersion, 0)
        val algorithm = preferences.getString(KeyAlgorithm, null).orEmpty()
        val passwordSalt = preferences.getString(KeyPasswordSalt, null).orEmpty()
        val passwordHash = preferences.getString(KeyPasswordHash, null).orEmpty()
        if (version <= 0 || algorithm.isBlank() || passwordSalt.isBlank() || passwordHash.isBlank()) {
            return null
        }

        return TabletAdminConfigMessage(
            version = version,
            updatedAt = preferences.getString(KeyUpdatedAt, null).orEmpty(),
            algorithm = algorithm,
            passwordSalt = passwordSalt,
            passwordHash = passwordHash
        )
    }

    fun save(message: TabletAdminConfigMessage) {
        if (message.version <= 0 ||
            message.algorithm.isBlank() ||
            message.passwordSalt.isBlank() ||
            message.passwordHash.isBlank()) {
            return
        }

        preferences.edit()
            .putInt(KeyVersion, message.version)
            .putString(KeyUpdatedAt, message.updatedAt)
            .putString(KeyAlgorithm, message.algorithm)
            .putString(KeyPasswordSalt, message.passwordSalt)
            .putString(KeyPasswordHash, message.passwordHash)
            .apply()
    }

    private companion object {
        private const val PreferencesName = "tablet_admin_config"
        private const val KeyVersion = "version"
        private const val KeyUpdatedAt = "updatedAt"
        private const val KeyAlgorithm = "algorithm"
        private const val KeyPasswordSalt = "passwordSalt"
        private const val KeyPasswordHash = "passwordHash"
    }
}
