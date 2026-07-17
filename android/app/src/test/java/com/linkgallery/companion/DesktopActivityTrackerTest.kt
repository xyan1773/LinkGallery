package com.linkgallery.companion

import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DesktopActivityTrackerTest {
    @Test
    fun concurrentHttpActivityAndStateReadsRemainConsistent() {
        val tracker = DesktopActivityTracker(activeLifetimeMillis = 1_000)
        val executor = Executors.newFixedThreadPool(8)
        try {
            val writes = (0 until 200).map { index ->
                executor.submit { tracker.record("desktop-${index % 4}", index.toLong()) }
            }
            val reads = (0 until 200).map { index ->
                executor.submit<Set<String>> { tracker.activeIds(index.toLong()) }
            }

            writes.forEach { it.get(2, TimeUnit.SECONDS) }
            reads.forEach { it.get(2, TimeUnit.SECONDS) }

            assertEquals(
                setOf("desktop-0", "desktop-1", "desktop-2", "desktop-3"),
                tracker.activeIds(200),
            )
            assertTrue(checkNotNull(tracker.nextExpiryDelayMillis(200)) in 996..999)
            assertTrue(tracker.activeIds(1_200).isEmpty())
        } finally {
            executor.shutdownNow()
        }
    }
}
