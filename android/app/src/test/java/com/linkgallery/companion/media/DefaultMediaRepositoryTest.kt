package com.linkgallery.companion.media

import java.io.ByteArrayInputStream
import java.time.Instant
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DefaultMediaRepositoryTest {
    @Test
    fun `maps MediaStore fields and returns a stable next cursor`() = runSuspend {
        val source = FakeDataSource(
            rows = listOf(
                row(id = 3, modified = 30, type = MediaType.IMAGE),
                row(id = 2, modified = 20, type = MediaType.VIDEO),
                row(id = 1, modified = 10, type = MediaType.IMAGE),
            ),
        )
        val repository = DefaultMediaRepository(source, FakePermissionGateway())

        val result = repository.getPage(MediaQuery(limit = 2))

        assertTrue(result is MediaPageResult.Success)
        val page = (result as MediaPageResult.Success).page
        assertEquals(2, page.items.size)
        assertEquals("photo-3.jpg", page.items[0].fileName)
        assertEquals(Instant.ofEpochMilli(30_500), page.items[0].takenAt)
        assertEquals(Instant.ofEpochSecond(30), page.items[0].modifiedAt)
        assertEquals(1920, page.items[0].width)
        assertEquals("Camera", page.items[0].albumName)
        assertFalse(page.items[0].id.contains("DCIM"))
        assertTrue(page.nextCursor?.startsWith("lgc2_") == true)
        assertTrue(page.hasMore)
        assertEquals(3, page.total)
        assertEquals(3, source.lastRequest?.limit)

        repository.getPage(MediaQuery(cursor = page.nextCursor, limit = 2))
        assertEquals(MediaStoreCursor(30_500, 2), source.lastRequest?.after)
    }

    @Test
    fun `cursor and explicit before parameters load stable non overlapping pages`() = runSuspend {
        val rows = (1L..15L).map { id ->
            row(id = id, modified = id, taken = 1_000L * id)
        }
        val repository = DefaultMediaRepository(FakeDataSource(rows), FakePermissionGateway())

        val first = (repository.getPage(MediaQuery(limit = 5)) as MediaPageResult.Success).page
        val secondByCursor = (
            repository.getPage(MediaQuery(cursor = first.nextCursor, limit = 5)) as
                MediaPageResult.Success
            ).page
        val secondByBefore = (
            repository.getPage(
                MediaQuery(before = MediaStoreCursor(11_000, 11), limit = 5),
            ) as MediaPageResult.Success
            ).page
        val third = (
            repository.getPage(MediaQuery(cursor = secondByCursor.nextCursor, limit = 5)) as
                MediaPageResult.Success
            ).page

        assertEquals(listOf(15L, 14L, 13L, 12L, 11L), first.items.map(::decodeTestMediaId))
        assertEquals(listOf(10L, 9L, 8L, 7L, 6L), secondByCursor.items.map(::decodeTestMediaId))
        assertEquals(secondByCursor.items.map(::decodeTestMediaId), secondByBefore.items.map(::decodeTestMediaId))
        assertFalse(first.items.map { it.id }.any { id -> id in secondByCursor.items.map { it.id } })
        assertTrue(first.hasMore)
        assertTrue(secondByCursor.hasMore)
        assertEquals(listOf(5L, 4L, 3L, 2L, 1L), third.items.map(::decodeTestMediaId))
        assertFalse(third.hasMore)
        assertNull(third.nextCursor)
    }

    @Test
    fun `returns empty success for an empty library`() = runSuspend {
        val repository = DefaultMediaRepository(FakeDataSource(), FakePermissionGateway())

        val result = repository.getPage(MediaQuery())

        val page = (result as MediaPageResult.Success).page
        assertTrue(page.items.isEmpty())
        assertNull(page.nextCursor)
        assertFalse(page.hasMore)
        assertEquals(0, page.total)
    }

    @Test
    fun `returns permissions without querying MediaStore`() = runSuspend {
        val source = FakeDataSource(rows = listOf(row()))
        val repository = DefaultMediaRepository(
            source,
            FakePermissionGateway(setOf("android.permission.READ_MEDIA_IMAGES")),
        )

        val result = repository.getPage(MediaQuery(types = setOf(MediaType.IMAGE)))

        assertEquals(
            MediaPageResult.PermissionDenied(setOf("android.permission.READ_MEDIA_IMAGES")),
            result,
        )
        assertNull(source.lastRequest)
    }

    @Test
    fun `rejects malformed cursors without querying MediaStore`() = runSuspend {
        val source = FakeDataSource()
        val repository = DefaultMediaRepository(source, FakePermissionGateway())

        val result = repository.getPage(MediaQuery(cursor = "not-a-cursor"))

        assertEquals(MediaPageResult.InvalidCursor, result)
        assertNull(source.lastRequest)
    }

    @Test
    fun `sync changes use generation cursor and expose generation`() = runSuspend {
        val source = FakeDataSource(
            rows = listOf(
                row(id = 1, modified = 10, generation = 11),
                row(id = 2, modified = 20, generation = 11),
            ),
        )
        val repository = DefaultMediaRepository(source, FakePermissionGateway())

        val state = repository.getSyncState() as MediaSyncStateResult.Success
        val changes = repository.getChanges(state.state.latestCursor, 10) as MediaChangesResult.Success

        assertTrue(state.state.latestCursor.startsWith("lgs1_"))
        assertTrue(changes.changes.upserts.isEmpty())
        val fromBeginning = repository.getChanges(null, 1) as MediaChangesResult.Success
        assertEquals(11L, fromBeginning.changes.upserts.single().generation)
        assertTrue(fromBeginning.changes.hasMore)
        assertEquals(
            MediaSyncCursor(11, 1),
            OpaqueMediaTokenCodec().decodeSyncCursor(fromBeginning.changes.nextCursor),
        )
        val second = repository.getChanges(
            fromBeginning.changes.nextCursor,
            1,
        ) as MediaChangesResult.Success
        assertEquals(2L, decodeTestMediaId(second.changes.upserts.single()))
    }

    @Test
    fun `api 29 sync cursor falls back to modified seconds`() = runSuspend {
        val repository = DefaultMediaRepository(
            FakeDataSource(rows = listOf(row(id = 7, modified = 123, generation = null))),
            FakePermissionGateway(),
        )

        val changes = repository.getChanges(null, 10) as MediaChangesResult.Success

        assertEquals(
            MediaSyncCursor(123, 7),
            OpaqueMediaTokenCodec().decodeSyncCursor(changes.changes.nextCursor),
        )
        assertNull(changes.changes.upserts.single().generation)
    }

    @Test
    fun `manifest pages lightweight ids without duplicate boundaries`() = runSuspend {
        val repository = DefaultMediaRepository(
            FakeDataSource(
                rows = listOf(
                    row(id = 1, generation = 21),
                    row(id = 2, generation = 22),
                ),
            ),
            FakePermissionGateway(),
        )

        val first = repository.getManifest(null, 1) as MediaManifestResult.Success
        val second = repository.getManifest(first.page.nextCursor, 1) as MediaManifestResult.Success

        assertEquals(1L, OpaqueMediaTokenCodec().decodeId(first.page.items.single().id)?.mediaStoreId)
        assertEquals(21L, first.page.items.single().generation)
        assertTrue(first.page.hasMore)
        assertEquals(2L, OpaqueMediaTokenCodec().decodeId(second.page.items.single().id)?.mediaStoreId)
        assertFalse(second.page.hasMore)
        assertNull(second.page.nextCursor)
    }

    @Test
    fun `returns not found when a previously listed item was removed`() = runSuspend {
        val source = FakeDataSource(rows = listOf(row(id = 42)))
        val repository = DefaultMediaRepository(source, FakePermissionGateway())
        val page = (repository.getPage(MediaQuery()) as MediaPageResult.Success).page
        val id = page.items.single().id
        source.rows = emptyList()

        val result = repository.getById(id)

        assertEquals(MediaItemResult.NotFound, result)
    }

    @Test
    fun `falls back to modified time when taken time is absent`() = runSuspend {
        val repository = DefaultMediaRepository(
            FakeDataSource(rows = listOf(row(taken = null, modified = 123))),
            FakePermissionGateway(),
        )

        val page = (repository.getPage(MediaQuery()) as MediaPageResult.Success).page

        assertEquals(Instant.ofEpochSecond(123), page.items.single().takenAt)
    }

    @Test
    fun `thumbnail entity tag uses modified time as generation`() = runSuspend {
        val repository = DefaultMediaRepository(
            FakeDataSource(rows = listOf(row(id = 7, taken = 30_500, modified = 123))),
            FakePermissionGateway(),
        )
        val id = (
            repository.getPage(MediaQuery()) as MediaPageResult.Success
            ).page.items.single().id

        val result = repository.getThumbnail(id, 256, 256) as MediaThumbnailResult.Found

        assertEquals("\"thumb-image-7-123-4096-256x256\"", result.entityTag)
    }

    @Test
    fun `thumbnail entity tag prefers MediaStore generation`() = runSuspend {
        val repository = DefaultMediaRepository(
            FakeDataSource(
                rows = listOf(row(id = 7, modified = 123, generation = 456)),
            ),
            FakePermissionGateway(),
        )
        val id = (
            repository.getPage(MediaQuery()) as MediaPageResult.Success
            ).page.items.single().id

        val result = repository.getThumbnail(id, 256, 256) as MediaThumbnailResult.Found

        assertEquals("\"thumb-image-7-456-4096-256x256\"", result.entityTag)
    }

    @Test
    fun `opens original content at requested offset after permission check`() = runSuspend {
        val source = FakeDataSource(rows = listOf(row(id = 7, type = MediaType.VIDEO, size = 10)))
        val repository = DefaultMediaRepository(source, FakePermissionGateway())
        val id = (
            repository.getPage(MediaQuery(types = setOf(MediaType.VIDEO))) as
                MediaPageResult.Success
            ).page.items.single().id

        val result = repository.getContent(id) as MediaContentResult.Found
        val bytes = result.content.open(3)!!.use { it.readBytes() }

        assertEquals(10L, result.content.length)
        assertEquals("video/mp4", result.content.contentType)
        assertTrue(result.content.entityTag!!.startsWith("\"sha256-"))
        assertEquals(3L, source.lastContentOffset)
        assertEquals("3456789", String(bytes))
    }

    @Test
    fun `rejects file paths instead of treating them as media IDs`() = runSuspend {
        val source = FakeDataSource(rows = listOf(row(id = 7, type = MediaType.VIDEO)))
        val repository = DefaultMediaRepository(source, FakePermissionGateway())

        val result = repository.getContent("../../DCIM/Camera/private.mp4")

        assertEquals(MediaContentResult.NotFound, result)
        assertNull(source.lastContentOffset)
    }

    @Test(expected = IllegalArgumentException::class)
    fun `rejects page sizes above protocol maximum`() {
        MediaQuery(limit = 201)
    }

    private class FakePermissionGateway(
        private val missing: Set<String> = emptySet(),
    ) : MediaPermissionGateway {
        override fun missingPermissions(types: Set<MediaType>): Set<String> = missing
    }

    private class FakeDataSource(
        var rows: List<MediaStoreRow> = emptyList(),
    ) : MediaStoreDataSource {
        var lastRequest: MediaStoreRequest? = null
        var lastContentOffset: Long? = null

        override fun query(request: MediaStoreRequest): List<MediaStoreRow> {
            lastRequest = request
            return rows
                .filter { it.type in request.types }
                .keysetPage(request.after, request.limit)
        }

        override fun count(types: Set<MediaType>): Int =
            rows.count { it.type in types }

        override fun libraryState(): MediaLibraryState = MediaLibraryState(
            libraryVersion = "library-test",
            latestCursor = rows
                .maxWithOrNull(
                    compareBy<MediaStoreRow> {
                        it.generation ?: it.dateModifiedEpochSeconds
                    }.thenBy { it.mediaStoreId },
                )
                ?.let {
                    MediaSyncCursor(
                        it.generation ?: it.dateModifiedEpochSeconds,
                        it.mediaStoreId,
                    )
                }
                ?: MediaSyncCursor(0, 0),
        )

        override fun queryChanges(
            after: MediaSyncCursor,
            limit: Int,
            types: Set<MediaType>,
        ): List<MediaStoreRow> = rows
            .asSequence()
            .filter { it.type in types }
            .filter {
                val value = it.generation ?: it.dateModifiedEpochSeconds
                value > after.value || (value == after.value && it.mediaStoreId > after.mediaStoreId)
            }
            .sortedWith(
                compareBy<MediaStoreRow> {
                    it.generation ?: it.dateModifiedEpochSeconds
                }.thenBy { it.mediaStoreId },
            )
            .take(limit)
            .toList()

        override fun queryManifest(
            afterId: Long?,
            limit: Int,
            types: Set<MediaType>,
        ): List<MediaManifestRow> = rows
            .asSequence()
            .filter { it.type in types }
            .filter { afterId == null || it.mediaStoreId > afterId }
            .sortedBy { it.mediaStoreId }
            .take(limit)
            .map { MediaManifestRow(it.mediaStoreId, it.type, it.generation) }
            .toList()

        override fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow? =
            rows.find { it.mediaStoreId == mediaStoreId && it.type == type }

        override fun loadThumbnail(
            mediaStoreId: Long,
            type: MediaType,
            width: Int,
            height: Int,
        ): ByteArray = byteArrayOf(1, 2, 3)

        override fun openContent(
            mediaStoreId: Long,
            type: MediaType,
            offset: Long,
        ): ByteArrayInputStream {
            lastContentOffset = offset
            val bytes = "0123456789".toByteArray()
            return ByteArrayInputStream(bytes, offset.toInt(), bytes.size - offset.toInt())
        }

        override fun getContentType(mediaStoreId: Long, type: MediaType): String =
            if (type == MediaType.VIDEO) "video/mp4" else "image/jpeg"
    }

    private companion object {
        fun row(
            id: Long = 1,
            modified: Long = 10,
            taken: Long? = 30_500,
            type: MediaType = MediaType.IMAGE,
            size: Long = 4_096,
            generation: Long? = null,
        ) = MediaStoreRow(
            mediaStoreId = id,
            fileName = "photo-$id.jpg",
            type = type,
            fileSize = size,
            dateTakenEpochMillis = taken,
            dateModifiedEpochSeconds = modified,
            width = 1920,
            height = 1080,
            durationMilliseconds = if (type == MediaType.VIDEO) 12_000 else null,
            albumName = "Camera",
            relativePath = "DCIM/Camera/",
            generationAdded = generation,
            generationModified = generation,
        )

        fun decodeTestMediaId(item: MediaRecord): Long =
            requireNotNull(
                item.fileName.removePrefix("photo-").removeSuffix(".jpg").toLongOrNull(),
            ) {
                "Unexpected test media file name: ${item.fileName}"
            }

        fun runSuspend(block: suspend () -> Unit) {
            var failure: Throwable? = null
            block.startCoroutine(
                object : Continuation<Unit> {
                    override val context = EmptyCoroutineContext

                    override fun resumeWith(result: Result<Unit>) {
                        failure = result.exceptionOrNull()
                    }
                },
            )
            failure?.let { throw it }
        }
    }
}
