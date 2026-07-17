package com.linkgallery.companion.server

import com.linkgallery.companion.pairing.AuthenticatedPairing
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferStatusRegistryTest {
    private val desktop = AuthenticatedPairing("desktop-1", "Studio PC")

    @Test
    fun acceptsSanitizedProgressAndExpiresItFromMemory() {
        var now = 1_000L
        val changes = mutableListOf<ActiveTransferStatus?>()
        val registry = TransferStatusRegistry({ now }, changes::add)

        val result = registry.update(desktop, update(expiresAt = 10_000L))

        assertTrue(result is TransferStatusResult.Accepted)
        assertEquals("Pictures", registry.current()?.destinationName)
        assertEquals("Studio PC", registry.current()?.desktopName)
        now = 10_000L
        assertNull(registry.current())
        assertNull(changes.last())
    }

    @Test
    fun rejectsPathLeakAndSequenceReplay() {
        val registry = TransferStatusRegistry(nowMillis = { 1_000L })
        val pathLeak = registry.update(
            desktop,
            update(destination = "C:\\Users\\Alice\\Pictures", expiresAt = 10_000L),
        )
        val accepted = registry.update(desktop, update(sequence = 2, expiresAt = 10_000L))
        val replay = registry.update(desktop, update(sequence = 2, expiresAt = 10_000L))

        assertEquals(400, (pathLeak as TransferStatusResult.Rejected).status)
        assertTrue(accepted is TransferStatusResult.Accepted)
        assertEquals("transfer_status_replayed", (replay as TransferStatusResult.Rejected).code)
    }

    @Test
    fun terminalStateClearsOnlyAuthenticatedDesktopStatus() {
        val registry = TransferStatusRegistry(nowMillis = { 1_000L })
        registry.update(desktop, update(expiresAt = 10_000L))

        val result = registry.update(
            desktop,
            update(state = "cancelled", sequence = 2, expiresAt = 10_000L),
        )

        assertTrue(result is TransferStatusResult.Accepted)
        assertNull(registry.current())
    }

    private fun update(
        destination: String = "Pictures",
        state: String = "running",
        sequence: Long = 1,
        expiresAt: Long,
    ) = TransferStatusUpdate(
        taskId = "task_1",
        destinationName = destination,
        completedItems = 2,
        totalItems = 5,
        completedBytes = 40,
        totalBytes = 100,
        state = state,
        sequence = sequence,
        expiresAtEpochMillis = expiresAt,
    )
}
