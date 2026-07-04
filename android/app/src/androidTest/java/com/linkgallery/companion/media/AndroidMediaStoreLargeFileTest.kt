package com.linkgallery.companion.media

import android.content.ContentUris
import android.content.ContentValues
import android.system.Os
import android.system.OsConstants
import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AndroidMediaStoreLargeFileTest {
    @Test
    fun generationChangesIncludeEveryVisibleManifestEntry() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val resolver = context.contentResolver
        val source = AndroidMediaStoreDataSource(context, resolver)
        val types = MediaType.entries.toSet()
        val baseline = source.libraryState().latestCursor
        val uri = checkNotNull(
            resolver.insert(
                MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY),
                ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, "generation-${System.nanoTime()}.jpg")
                    put(MediaStore.MediaColumns.MIME_TYPE, "image/jpeg")
                    put(MediaStore.MediaColumns.RELATIVE_PATH, "Pictures/LinkGalleryTests")
                    put(MediaStore.MediaColumns.IS_PENDING, 1)
                },
            ),
        )
        try {
            resolver.openOutputStream(uri).use { output ->
                checkNotNull(output).write(byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xD9.toByte()))
            }
            resolver.update(
                uri,
                ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) },
                null,
                null,
            )
            val id = ContentUris.parseId(uri)
            val manifest = source.queryManifest(id - 1, 2, types)
            val changes = source.queryChanges(baseline, 10, types)

            assertTrue(manifest.any { it.mediaStoreId == id })
            assertTrue(changes.any { it.mediaStoreId == id })
        } finally {
            resolver.delete(uri, null, null)
        }
    }

    @Test
    fun readsFromOffsetBeyondTwoGigabytesWithoutLoadingTheFile() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val resolver = context.contentResolver
        val collection = MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, "linkgallery-range-${System.nanoTime()}.mp4")
            put(MediaStore.MediaColumns.MIME_TYPE, "video/mp4")
            put(MediaStore.MediaColumns.RELATIVE_PATH, "Movies/LinkGalleryTests")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = checkNotNull(resolver.insert(collection, values))

        try {
            resolver.openFileDescriptor(uri, "rw").use { descriptor ->
                checkNotNull(descriptor)
                Os.lseek(descriptor.fileDescriptor, FILE_SIZE - 1, OsConstants.SEEK_SET)
                assertEquals(
                    1,
                    Os.write(descriptor.fileDescriptor, byteArrayOf(MARKER), 0, 1),
                )
            }
            resolver.update(
                uri,
                ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) },
                null,
                null,
            )

            val mediaStoreId = ContentUris.parseId(uri)
            val source = AndroidMediaStoreDataSource(context, resolver)
            val row = checkNotNull(source.find(mediaStoreId, MediaType.VIDEO))

            assertEquals(FILE_SIZE, row.fileSize)
            source.openContent(mediaStoreId, MediaType.VIDEO, FILE_SIZE - 1).use { stream ->
                assertEquals(MARKER.toInt() and 0xFF, checkNotNull(stream).read())
                assertEquals(-1, stream.read())
            }
        } finally {
            resolver.delete(uri, null, null)
        }
    }

    private companion object {
        const val FILE_SIZE = 3L * 1024 * 1024 * 1024
        const val MARKER: Byte = 0x5A
    }
}
