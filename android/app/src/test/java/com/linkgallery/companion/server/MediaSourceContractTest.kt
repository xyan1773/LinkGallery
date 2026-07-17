package com.linkgallery.companion.server

import com.linkgallery.companion.media.DefaultMediaRepository
import com.linkgallery.companion.media.MediaPermissionGateway
import com.linkgallery.companion.media.MediaStoreDataSource
import com.linkgallery.companion.media.MediaStoreRequest
import com.linkgallery.companion.media.MediaStoreRow
import com.linkgallery.companion.media.MediaType
import com.linkgallery.companion.pairing.AllowAllAccessTokenAuthenticator
import java.util.concurrent.CountDownLatch
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MediaSourceContractTest {
    @Test
    fun repositoryClassificationFlowsThroughHttpContractWithoutNullPhoneSource() {
        val rows = listOf(
            row(
                id = 2,
                fileName = "DJI_20260717_101530.jpg",
                ownerPackageName = "com.dji.mimo",
                metadataMake = "DJI",
                metadataModel = "Osmo Pocket 3",
            ),
            row(id = 1, fileName = "IMG_0001.jpg"),
        )
        val source = object : MediaStoreDataSource {
            override fun query(request: MediaStoreRequest): List<MediaStoreRow> = rows.take(request.limit)
            override fun count(types: Set<MediaType>, albumId: String?): Int = rows.size
            override fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow? =
                rows.firstOrNull { it.mediaStoreId == mediaStoreId && it.type == type }
        }
        val repository = DefaultMediaRepository(
            source,
            object : MediaPermissionGateway {
                override fun missingPermissions(types: Set<MediaType>): Set<String> = emptySet()
            },
        )
        val controller = ApiController(
            publicDeviceInfoProvider = PublicDeviceInfoProvider {
                PublicDeviceInfo("id", "Pixel", "Google", "Pixel", 1, "test", "instance", false, "AA")
            },
            deviceInfoProvider = DeviceInfoProvider {
                DeviceInfoResult.Success(DeviceInfo("id", "Pixel", "Pixel", 80, rows.size))
            },
            mediaRepository = repository,
            accessTokenAuthenticator = AllowAllAccessTokenAuthenticator,
        )

        val response = runSuspend { controller.handle("GET", "/api/v1/media") }

        assertEquals(200, response.status)
        assertTrue(response.body.contains("\"sourceDevice\":\"DJI Pocket 3\""))
        assertTrue(response.body.contains("\"sourceApplication\":\"DJI Mimo\""))
        assertTrue(response.body.contains("\"sourceDevice\":\"Phone\""))
        assertFalse(response.body.contains("\"sourceDevice\":null"))
    }

    private fun row(
        id: Long,
        fileName: String,
        ownerPackageName: String? = null,
        metadataMake: String? = null,
        metadataModel: String? = null,
    ) = MediaStoreRow(
        mediaStoreId = id,
        fileName = fileName,
        type = MediaType.IMAGE,
        fileSize = 1024,
        dateTakenEpochMillis = 1_752_749_730_000,
        dateModifiedEpochSeconds = 1_752_749_730,
        width = 4000,
        height = 3000,
        durationMilliseconds = null,
        albumName = "Camera",
        relativePath = "DCIM/Camera/",
        albumId = "camera",
        mimeType = "image/jpeg",
        ownerPackageName = ownerPackageName,
        metadataMake = metadataMake,
        metadataModel = metadataModel,
    )

    private fun <T> runSuspend(block: suspend () -> T): T {
        val completed = CountDownLatch(1)
        var outcome: Result<T>? = null
        block.startCoroutine(
            object : Continuation<T> {
                override val context = EmptyCoroutineContext
                override fun resumeWith(result: Result<T>) {
                    outcome = result
                    completed.countDown()
                }
            },
        )
        completed.await()
        return checkNotNull(outcome).getOrThrow()
    }
}
