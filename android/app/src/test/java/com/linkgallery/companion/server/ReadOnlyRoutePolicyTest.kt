package com.linkgallery.companion.server

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReadOnlyRoutePolicyTest {
    @Test
    fun mediaReadsAreAllowed() {
        assertTrue(ReadOnlyRoutePolicy.permits("GET", "/api/v1/media"))
        assertTrue(ReadOnlyRoutePolicy.permits("GET", "/api/v1/device"))
        assertTrue(ReadOnlyRoutePolicy.permits("GET", "/api/v1/media/media-1/content"))
    }

    @Test
    fun mediaWritesAreRejected() {
        assertFalse(ReadOnlyRoutePolicy.permits("DELETE", "/api/v1/media"))
        assertFalse(ReadOnlyRoutePolicy.permits("PATCH", "/api/v1/media"))
        assertFalse(ReadOnlyRoutePolicy.permits("PUT", "/api/v1/media"))
        assertFalse(ReadOnlyRoutePolicy.permits("POST", "/api/v1/media/upload"))
        assertFalse(ReadOnlyRoutePolicy.permits("DELETE", "/api/v1/media/media-1/content"))
    }

    @Test
    fun pairingControlRoutesAreAllowedWithoutOpeningMediaWrites() {
        assertTrue(ReadOnlyRoutePolicy.permits("POST", "/api/v1/pair/request"))
        assertTrue(ReadOnlyRoutePolicy.permits("POST", "/api/v1/pair/confirm"))
        assertFalse(ReadOnlyRoutePolicy.permits("PUT", "/api/v1/pair/request"))
    }
}
