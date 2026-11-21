package com.rentalsystem.android.fresh

object FreshUserPackageList {
    fun parse(raw: String?): List<String> =
        raw
            .orEmpty()
            .split(',', ';', '\n', '\r', '\t', ' ')
            .asSequence()
            .map { it.trim() }
            .filter { it.isNotBlank() }
            .distinct()
            .toList()
}
