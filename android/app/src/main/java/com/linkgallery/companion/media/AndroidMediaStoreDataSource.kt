package com.linkgallery.companion.media

import android.content.ContentResolver
import android.content.ContentUris
import android.content.Context
import android.database.Cursor
import android.graphics.Bitmap
import android.os.Build
import android.provider.MediaStore
import android.util.Size
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream

class AndroidMediaStoreDataSource(
    private val context: Context,
    private val contentResolver: ContentResolver,
) : MediaStoreDataSource {
    override fun query(request: MediaStoreRequest): List<MediaStoreRow> {
        val spec = request.toMediaStoreQuerySpec()
        return contentResolver.query(
            COLLECTION,
            projection,
            spec.selection,
            spec.arguments.toTypedArray(),
            spec.sortOrder,
        )?.use { cursor ->
            buildList {
                while (size < spec.limit && cursor.moveToNext()) {
                    add(cursor.toRow())
                }
            }
        }.orEmpty()
    }

    override fun count(types: Set<MediaType>, albumId: String?): Int {
        val (selection, arguments) = selectionFor(types, albumId)
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

    override fun libraryState(): MediaLibraryState =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            MediaLibraryState(
                libraryVersion = MediaStore.getVersion(context, MediaStore.VOLUME_EXTERNAL_PRIMARY),
                latestCursor = MediaSyncCursor(
                    MediaStore.getGeneration(context, MediaStore.VOLUME_EXTERNAL_PRIMARY),
                    Long.MAX_VALUE,
                ),
            )
        } else {
            MediaLibraryState(
                libraryVersion = MediaStore.getVersion(context),
                latestCursor = latestModifiedCursor(),
            )
        }

    override fun queryChanges(
        after: MediaSyncCursor,
        limit: Int,
        types: Set<MediaType>,
    ): List<MediaStoreRow> {
        val (typeSelection, typeArguments) = selectionFor(types)
        val arguments = typeArguments.toMutableList()
        val selection: String
        val sortOrder: String
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val generation = generationExpression()
            selection =
                "$typeSelection AND (" +
                "$GENERATION_ADDED > ? OR $GENERATION_MODIFIED > ? OR " +
                "(($GENERATION_ADDED = ? OR $GENERATION_MODIFIED = ?) AND $ID > ?))"
            arguments += after.value.toString()
            arguments += after.value.toString()
            arguments += after.value.toString()
            arguments += after.value.toString()
            arguments += after.mediaStoreId.toString()
            sortOrder =
                "$generation ASC, $ID ASC"
        } else {
            selection =
                "$typeSelection AND ($DATE_MODIFIED > ? OR ($DATE_MODIFIED = ? AND $ID > ?))"
            arguments += after.value.toString()
            arguments += after.value.toString()
            arguments += after.mediaStoreId.toString()
            sortOrder = "$DATE_MODIFIED ASC, $ID ASC"
        }
        return contentResolver.query(
            COLLECTION,
            projection,
            selection,
            arguments.toTypedArray(),
            sortOrder,
        )?.use { cursor ->
            buildList {
                while (size < limit && cursor.moveToNext()) add(cursor.toRow())
            }
        }.orEmpty()
    }

    override fun queryManifest(
        afterId: Long?,
        limit: Int,
        types: Set<MediaType>,
    ): List<MediaManifestRow> {
        val (typeSelection, typeArguments) = selectionFor(types)
        val selection = if (afterId == null) typeSelection else "$typeSelection AND $ID > ?"
        val arguments = typeArguments.toMutableList()
        if (afterId != null) arguments += afterId.toString()
        val manifestProjection = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            arrayOf(ID, MEDIA_TYPE, GENERATION_ADDED, GENERATION_MODIFIED)
        } else {
            arrayOf(ID, MEDIA_TYPE)
        }
        return contentResolver.query(
            COLLECTION,
            manifestProjection,
            selection,
            arguments.toTypedArray(),
            "$ID ASC",
        )?.use { cursor ->
            buildList {
                while (size < limit && cursor.moveToNext()) {
                    val type = when (cursor.getInt(cursor.column(MEDIA_TYPE))) {
                        MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE -> MediaType.IMAGE
                        MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO -> MediaType.VIDEO
                        else -> continue
                    }
                    add(
                        MediaManifestRow(
                            cursor.getLong(cursor.column(ID)),
                            type,
                            listOfNotNull(
                                cursor.nullableLongIfPresent(GENERATION_ADDED),
                                cursor.nullableLongIfPresent(GENERATION_MODIFIED),
                            ).maxOrNull(),
                        ),
                    )
                }
            }
        }.orEmpty()
    }

    override fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow? {
        val uri = ContentUris.withAppendedId(COLLECTION, mediaStoreId)
        val selection = "$MEDIA_TYPE = ?"
        val arguments = arrayOf(type.mediaStoreValue.toString())
        return contentResolver.query(uri, projection, selection, arguments, null)?.use { cursor ->
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
        val mediaUri = ContentUris.withAppendedId(COLLECTION, mediaStoreId)
        val uri = if (type == MediaType.IMAGE && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.setRequireOriginal(mediaUri)
        } else {
            mediaUri
        }
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
        albumId: String? = null,
    ): Pair<String, List<String>> {
        val arguments = mutableListOf<String>()
        val mediaTypePlaceholders = types.joinToString(",") { type ->
            arguments += type.mediaStoreValue.toString()
            "?"
        }
        val selection = buildString {
            append("$MEDIA_TYPE IN ($mediaTypePlaceholders)")
            if (albumId != null) {
                append(" AND $BUCKET_ID = ?")
                arguments += albumId
            }
        }
        return selection to arguments
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
            albumId = nullableString(BUCKET_ID),
            albumName = nullableString(BUCKET_DISPLAY_NAME),
            relativePath = nullableString(RELATIVE_PATH),
            generationAdded = nullableLongIfPresent(GENERATION_ADDED),
            generationModified = nullableLongIfPresent(GENERATION_MODIFIED),
        )
    }

    private fun Cursor.column(name: String): Int = getColumnIndexOrThrow(name)

    private fun Cursor.nullableLong(name: String): Long? =
        column(name).let { index -> if (isNull(index)) null else getLong(index) }

    private fun Cursor.nullableInt(name: String): Int? =
        column(name).let { index -> if (isNull(index)) null else getInt(index) }

    private fun Cursor.nullableString(name: String): String? =
        column(name).let { index -> if (isNull(index)) null else getString(index) }

    private fun Cursor.nullableLongIfPresent(name: String): Long? {
        val index = getColumnIndex(name)
        return if (index < 0 || isNull(index)) null else getLong(index)
    }

    private fun latestModifiedCursor(): MediaSyncCursor =
        contentResolver.query(
            COLLECTION,
            arrayOf(DATE_MODIFIED, ID),
            null,
            null,
            "$DATE_MODIFIED DESC, $ID DESC",
        )?.use { cursor ->
            if (cursor.moveToFirst()) {
                MediaSyncCursor(cursor.getLong(0), cursor.getLong(1))
            } else {
                MediaSyncCursor(0, 0)
            }
        } ?: MediaSyncCursor(0, 0)

    private fun generationExpression(): String =
        "CASE WHEN $GENERATION_MODIFIED > $GENERATION_ADDED " +
            "THEN $GENERATION_MODIFIED ELSE $GENERATION_ADDED END"

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
        const val BUCKET_ID = MediaStore.Images.ImageColumns.BUCKET_ID
        const val BUCKET_DISPLAY_NAME = MediaStore.Images.ImageColumns.BUCKET_DISPLAY_NAME
        const val RELATIVE_PATH = MediaStore.MediaColumns.RELATIVE_PATH
        const val MEDIA_TYPE = MediaStore.Files.FileColumns.MEDIA_TYPE
        const val GENERATION_ADDED = MediaStore.MediaColumns.GENERATION_ADDED
        const val GENERATION_MODIFIED = MediaStore.MediaColumns.GENERATION_MODIFIED
        val BASE_PROJECTION = arrayOf(
            ID,
            DISPLAY_NAME,
            SIZE,
            DATE_TAKEN,
            DATE_MODIFIED,
            WIDTH,
            HEIGHT,
            DURATION,
            BUCKET_ID,
            BUCKET_DISPLAY_NAME,
            RELATIVE_PATH,
            MEDIA_TYPE,
        )
    }

    private val projection: Array<String>
        get() = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            BASE_PROJECTION + arrayOf(GENERATION_ADDED, GENERATION_MODIFIED)
        } else {
            BASE_PROJECTION
        }
}

internal data class MediaStoreQuerySpec(
    val selection: String,
    val arguments: List<String>,
    val sortOrder: String,
    val limit: Int,
)

internal fun MediaStoreRequest.toMediaStoreQuerySpec(): MediaStoreQuerySpec {
    val arguments = types
        .map { type ->
            when (type) {
                MediaType.IMAGE -> MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE
                MediaType.VIDEO -> MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO
            }.toString()
        }
        .toMutableList()
    val typePlaceholders = types.joinToString(",") { "?" }
    val effectiveSortTime = """
        CASE
            WHEN ${MediaStore.Images.ImageColumns.DATE_TAKEN} IS NOT NULL
                 AND ${MediaStore.Images.ImageColumns.DATE_TAKEN} > 0
            THEN ${MediaStore.Images.ImageColumns.DATE_TAKEN}
            ELSE ${MediaStore.MediaColumns.DATE_MODIFIED} * 1000
        END
    """.trimIndent().replace(Regex("\\s+"), " ")
    val selection = buildString {
        append("${MediaStore.Files.FileColumns.MEDIA_TYPE} IN ($typePlaceholders)")
        albumId?.let {
            append(" AND ${MediaStore.Images.ImageColumns.BUCKET_ID} = ?")
            arguments += it
        }
        after?.let { cursor ->
            append(" AND ((")
            append("${MediaStore.Images.ImageColumns.DATE_TAKEN} IS NOT NULL")
            append(" AND ${MediaStore.Images.ImageColumns.DATE_TAKEN} > 0")
            append(" AND (${MediaStore.Images.ImageColumns.DATE_TAKEN} < ?")
            append(" OR (${MediaStore.Images.ImageColumns.DATE_TAKEN} = ?")
            append(" AND ${MediaStore.MediaColumns._ID} < ?)))")
            append(" OR ((")
            append("${MediaStore.Images.ImageColumns.DATE_TAKEN} IS NULL")
            append(" OR ${MediaStore.Images.ImageColumns.DATE_TAKEN} <= 0)")
            append(" AND (${MediaStore.MediaColumns.DATE_MODIFIED} < ?")
            append(" OR (${MediaStore.MediaColumns.DATE_MODIFIED} = ?")
            append(" AND ${MediaStore.MediaColumns._ID} < ?))))")
            arguments += cursor.sortTimestampEpochMillis.toString()
            arguments += cursor.sortTimestampEpochMillis.toString()
            arguments += cursor.mediaStoreId.toString()
            arguments += (cursor.sortTimestampEpochMillis / 1000).toString()
            arguments += (cursor.sortTimestampEpochMillis / 1000).toString()
            arguments += cursor.mediaStoreId.toString()
        }
    }
    return MediaStoreQuerySpec(
        selection = selection,
        arguments = arguments,
        sortOrder = "$effectiveSortTime DESC, ${MediaStore.MediaColumns._ID} DESC",
        limit = this.limit,
    )
}
