package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaPage
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaType
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Test

class ProtocolContractFixtureTest {
    @Test
    fun androidMediaPageSerializerMatchesSharedWindowsFixture() {
        val page = MediaPage(
            items = listOf(
                MediaRecord(
                    id = "fixture-media-1",
                    fileName = "DJI_20260717_101530.mp4",
                    type = MediaType.VIDEO,
                    fileSize = 4_294_967_296,
                    width = 3840,
                    height = 2160,
                    durationMilliseconds = 125_000,
                    takenAt = Instant.parse("2026-07-17T10:15:30Z"),
                    modifiedAt = Instant.parse("2026-07-17T10:17:35Z"),
                    albumId = "camera",
                    albumName = "Camera",
                    relativePath = "DCIM/DJI Album",
                    generation = 42,
                    thumbnailUrl = "/api/v1/media/fixture-media-1/thumbnail?size=256",
                    sourceDevice = "DJI Pocket 3",
                    sourceApplication = "DJI Mimo",
                    isEditedExport = false,
                ),
            ),
            nextCursor = null,
            hasMore = false,
            total = 1,
        )

        assertEquals(fixture("media-page.json"), Json.mediaPage(page))
    }

    @Test
    fun normalizedProblemSerializerMatchesSharedWindowsFixture() {
        assertEquals(
            fixture("problem.json"),
            Json.problem("permission_denied", "Media permission required"),
        )
    }

    private fun fixture(name: String): String = checkNotNull(javaClass.getResource("/$name"))
        .readText()
        .trim()
}
