package com.linkgallery.companion.media

import android.provider.MediaStore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AndroidMediaStoreQuerySpecTest {
    @Test
    fun `first page pushes stable ordering and bounded read to MediaStore`() {
        val spec = MediaStoreRequest(
            after = null,
            limit = 101,
            types = setOf(MediaType.IMAGE, MediaType.VIDEO),
        ).toMediaStoreQuerySpec()

        assertEquals(101, spec.limit)
        assertEquals(listOf("1", "3"), spec.arguments)
        assertFalse(spec.selection.contains("_id < ?"))
        assertTrue(spec.sortOrder.endsWith("date_modified * 1000 END DESC, _id DESC"))
    }

    @Test
    fun `next page pushes keyset predicate below the previous cursor`() {
        val spec = MediaStoreRequest(
            after = MediaStoreCursor(1_782_500_000_000, 37_120),
            limit = 201,
            types = setOf(MediaType.IMAGE),
        ).toMediaStoreQuerySpec()

        assertEquals(
            listOf(
                "1",
                "1782500000000",
                "1782500000000",
                "37120",
                "1782500000",
                "1782500000",
                "37120",
            ),
            spec.arguments,
        )
        assertTrue(
            spec.selection.contains("${MediaStore.Images.ImageColumns.DATE_TAKEN} < ?"),
        )
        assertTrue(
            spec.selection.contains("${MediaStore.MediaColumns.DATE_MODIFIED} < ?"),
        )
        assertTrue(spec.selection.contains("_id < ?"))
        assertEquals(201, spec.limit)
    }
}
