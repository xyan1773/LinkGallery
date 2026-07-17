package com.linkgallery.companion

import org.junit.Assert.assertEquals
import org.junit.Test

class ServiceReconnectPolicyTest {
    @Test
    fun retriesBackOffAndCapWithoutPollingRapidly() {
        val policy = ServiceReconnectPolicy(initialDelayMillis = 500, maximumDelayMillis = 4_000)

        assertEquals(listOf(500L, 1_000L, 2_000L, 4_000L, 4_000L), List(5) {
            policy.nextDelayMillis()
        })
    }

    @Test
    fun successfulNetworkRefreshResetsBackoff() {
        val policy = ServiceReconnectPolicy(initialDelayMillis = 250, maximumDelayMillis = 2_000)
        policy.nextDelayMillis()
        policy.nextDelayMillis()

        policy.reset()

        assertEquals(250L, policy.nextDelayMillis())
    }
}
