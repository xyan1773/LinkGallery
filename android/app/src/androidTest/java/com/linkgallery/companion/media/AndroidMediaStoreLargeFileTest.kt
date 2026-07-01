package com.linkgallery.companion.media

import android.content.ContentUris
import android.content.ContentValues
import android.system.Os
import android.system.OsConstants
import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AndroidMediaStoreLargeFileTest {
    @Test
    fun readsFromOffsetBeyondTwoGigabytesWithoutLoadingTheFile() {
        val resolver = InstrumentationRegistry.getInstrumentation().targetContext.contentResolver
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
            val source = AndroidMediaStoreDataSource(resolver)
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
