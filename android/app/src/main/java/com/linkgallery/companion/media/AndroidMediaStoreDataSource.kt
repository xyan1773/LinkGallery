package com.linkgallery.companion.media

import android.content.ContentResolver
import android.content.ContentUris
import android.database.Cursor
import android.graphics.Bitmap
import android.os.Bundle
import android.provider.MediaStore
import android.util.Size
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream

class AndroidMediaStoreDataSource(
    private val contentResolver: ContentResolver,
) : MediaStoreDataSource {
    override fun query(request: MediaStoreRequest): List<MediaStoreRow> {
        val (selection, arguments) = selectionFor(request.types)
        val queryArguments = Bundle().apply {
            putString(ContentResolver.QUERY_ARG_SQL_SELECTION, selection)
            putStringArray(ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, arguments.toTypedArray())
        }

        return contentResolver.query(
            COLLECTION,
            PROJECTION,
            queryArguments,
            null,
        )?.use { cursor ->
            buildList {
                while (cursor.moveToNext()) {
                    add(cursor.toRow())
                }
            }
        }.orEmpty()
            .keysetPage(request.after, request.limit)
    }

    override fun count(types: Set<MediaType>): Int {
        val (selection, arguments) = selectionFor(types)
        return contentResolver.query(
            COLLECTION,
            arrayOf(ID),
            selection,
            arguments.toTypedArray(),
            null,
        )?.use { cursor ->
            cursor.count
        } ?: 0
    }

    override fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow? {
        val uri = ContentUris.withAppendedId(COLLECTION, mediaStoreId)
        val selection = "$MEDIA_TYPE = ?"
        val arguments = arrayOf(type.mediaStoreValue.toString())
        return contentResolver.query(uri, PROJECTION, selection, arguments, null)?.use { cursor ->
            if (cursor.moveToFirst()) cursor.toRow() else null
        }
    }

    override fun loadThumbnail(
        mediaStoreId: Long,
        type: MediaType,
        width: Int,
        height: Int,
    ): ByteArray? {
        val uri = ContentUris.withAppendedId(COLLECTION, mediaStoreId)
        return try {
            val bitmap = contentResolver.loadThumbnail(uri, Size(width, height), null)
            try {
                ByteArrayOutputStream().use { output ->
                    if (!bitmap.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, output)) {
                        null
                    } else {
                        output.toByteArray()
                    }
                }
            } finally {
                bitmap.recycle()
            }
        } catch (_: IOException) {
            null
        }
    }

    override fun openContent(
        mediaStoreId: Long,
        type: MediaType,
        offset: Long,
    ): InputStream? {
        val uri = ContentUris.withAppendedId(COLLECTION, mediaStoreId)
        var descriptor: android.content.res.AssetFileDescriptor? = null
        return try {
            descriptor = contentResolver.openAssetFileDescriptor(uri, "r") ?: return null
            val stream = descriptor.createInputStream()
            var remaining = offset
            while (remaining > 0) {
                val skipped = stream.skip(remaining)
                if (skipped > 0) {
                    remaining -= skipped
                } else if (stream.read() >= 0) {
                    remaining--
                } else {
                    stream.close()
                    return null
                }
            }
            stream
        } catch (_: IOException) {
            descriptor?.close()
            null
        } catch (exception: Exception) {
            descriptor?.close()
            throw exception
        }
    }

    override fun getContentType(mediaStoreId: Long, type: MediaType): String? =
        contentResolver.getType(ContentUris.withAppendedId(COLLECTION, mediaStoreId))

    private fun selectionFor(
        types: Set<MediaType>,
    ): Pair<String, List<String>> {
        val arguments = mutableListOf<String>()
        val mediaTypePlaceholders = types.joinToString(",") { type ->
            arguments += type.mediaStoreValue.toString()
            "?"
        }
        return "$MEDIA_TYPE IN ($mediaTypePlaceholders)" to arguments
    }

    private fun Cursor.toRow(): MediaStoreRow {
        val type = when (getInt(column(MEDIA_TYPE))) {
            MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE -> MediaType.IMAGE
            MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO -> MediaType.VIDEO
            else -> error("MediaStore returned a non-image/video row.")
        }
        return MediaStoreRow(
            mediaStoreId = getLong(column(ID)),
            fileName = getString(column(DISPLAY_NAME)).orEmpty(),
            type = type,
            fileSize = getLong(column(SIZE)),
            dateTakenEpochMillis = nullableLong(DATE_TAKEN),
            dateModifiedEpochSeconds = getLong(column(DATE_MODIFIED)),
            width = nullableInt(WIDTH),
            height = nullableInt(HEIGHT),
            durationMilliseconds = if (type == MediaType.VIDEO) nullableLong(DURATION) else null,
            albumName = nullableString(BUCKET_DISPLAY_NAME),
            relativePath = nullableString(RELATIVE_PATH),
        )
    }

    private fun Cursor.column(name: String): Int = getColumnIndexOrThrow(name)

    private fun Cursor.nullableLong(name: String): Long? =
        column(name).let { index -> if (isNull(index)) null else getLong(index) }

    private fun Cursor.nullableInt(name: String): Int? =
        column(name).let { index -> if (isNull(index)) null else getInt(index) }

    private fun Cursor.nullableString(name: String): String? =
        column(name).let { index -> if (isNull(index)) null else getString(index) }

    private val MediaType.mediaStoreValue: Int
        get() = when (this) {
            MediaType.IMAGE -> MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE
            MediaType.VIDEO -> MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO
        }

    private companion object {
        const val JPEG_QUALITY = 85
        val COLLECTION = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL)

        const val ID = MediaStore.MediaColumns._ID
        const val DISPLAY_NAME = MediaStore.MediaColumns.DISPLAY_NAME
        const val SIZE = MediaStore.MediaColumns.SIZE
        const val DATE_TAKEN = MediaStore.Images.ImageColumns.DATE_TAKEN
        const val DATE_MODIFIED = MediaStore.MediaColumns.DATE_MODIFIED
        const val WIDTH = MediaStore.MediaColumns.WIDTH
        const val HEIGHT = MediaStore.MediaColumns.HEIGHT
        const val DURATION = MediaStore.Video.VideoColumns.DURATION
        const val BUCKET_DISPLAY_NAME = MediaStore.Images.ImageColumns.BUCKET_DISPLAY_NAME
        const val RELATIVE_PATH = MediaStore.MediaColumns.RELATIVE_PATH
        const val MEDIA_TYPE = MediaStore.Files.FileColumns.MEDIA_TYPE
        val PROJECTION = arrayOf(
            ID,
            DISPLAY_NAME,
            SIZE,
            DATE_TAKEN,
            DATE_MODIFIED,
            WIDTH,
            HEIGHT,
            DURATION,
            BUCKET_DISPLAY_NAME,
            RELATIVE_PATH,
            MEDIA_TYPE,
        )
    }
}
