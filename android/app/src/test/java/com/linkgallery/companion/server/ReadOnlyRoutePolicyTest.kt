package com.linkgallery.companion.server

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReadOnlyRoutePolicyTest {
    @Test
    fun mediaReadsAreAllowed() {
        assertTrue(ReadOnlyRoutePolicy.permits("GET", "/api/v1/media"))
    }

    @Test
    fun mediaWritesAreRejected() {
        assertFalse(ReadOnlyRoutePolicy.permits("DELETE", "/api/v1/media"))
        assertFalse(ReadOnlyRoutePolicy.permits("POST", "/api/v1/media/upload"))
    }
}

