package com.rentalsystem.android.fresh

import org.junit.Assert.assertEquals
import org.junit.Test

class FreshUserPackageListTest {
    @Test
    fun parseAcceptsCommonSeparatorsAndDeduplicates() {
        assertEquals(
            listOf("com.mobile.legends", "com.roblox.client", "com.garena.game.codm"),
            FreshUserPackageList.parse(
                " com.mobile.legends,com.roblox.client\ncom.mobile.legends;com.garena.game.codm "
            )
        )
    }

    @Test
    fun parseBlankInputReturnsEmptyList() {
        assertEquals(emptyList<String>(), FreshUserPackageList.parse(" , \n\t "))
        assertEquals(emptyList<String>(), FreshUserPackageList.parse(null))
    }
}
