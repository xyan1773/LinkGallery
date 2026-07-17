package com.linkgallery.companion.media

import java.io.InputStream

data class MediaStoreCursor(
    val sortTimestampEpochMillis: Long,
    val mediaStoreId: Long,
)

data class MediaSyncCursor(
    val value: Long,
    val mediaStoreId: Long,
)

data class MediaStoreRequest(
    val after: MediaStoreCursor?,
    val limit: Int,
    val types: Set<MediaType>,
    val albumId: String? = null,
)

data class MediaStoreRow(
    val mediaStoreId: Long,
    val fileName: String,
    val type: MediaType,
    val fileSize: Long,
    val dateTakenEpochMillis: Long?,
    val dateModifiedEpochSeconds: Long,
    val width: Int?,
    val height: Int?,
    val durationMilliseconds: Long?,
    val albumName: String?,
    val relativePath: String?,
    val albumId: String? = null,
    val generationAdded: Long? = null,
    val generationModified: Long? = null,
    val mimeType: String? = null,
    val ownerPackageName: String? = null,
) {
    val sortTimestampEpochMillis: Long
        get() = dateTakenEpochMillis?.takeIf { it > 0 } ?: dateModifiedEpochSeconds * 1000

    val generation: Long?
        get() = listOfNotNull(generationAdded, generationModified).maxOrNull()
}

data class MediaLibraryState(
    val libraryVersion: String,
    val latestCursor: MediaSyncCursor,
)

data class MediaManifestRow(
    val mediaStoreId: Long,
    val type: MediaType,
    val generation: Long?,
)

internal fun Iterable<MediaStoreRow>.keysetPage(
    after: MediaStoreCursor?,
    limit: Int,
): List<MediaStoreRow> =
    asSequence()
        .filter { row -> after == null || row.isBefore(after) }
        .sortedWith(
            compareByDescending<MediaStoreRow> { it.sortTimestampEpochMillis }
                .thenByDescending { it.mediaStoreId },
        )
        .take(limit)
        .toList()

private fun MediaStoreRow.isBefore(cursor: MediaStoreCursor): Boolean =
    sortTimestampEpochMillis < cursor.sortTimestampEpochMillis ||
        (
            sortTimestampEpochMillis == cursor.sortTimestampEpochMillis &&
                mediaStoreId < cursor.mediaStoreId
            )

/**
 * Injectable, read-only boundary around Android's MediaStore.
 *
 * Implementations must return rows ordered by taken time (falling back to modified time)
 * and ID, both descending.
 */
interface MediaStoreDataSource {
    fun query(request: MediaStoreRequest): List<MediaStoreRow>

    fun count(types: Set<MediaType>, albumId: String? = null): Int

    fun libraryState(): MediaLibraryState =
        MediaLibraryState("test-library", MediaSyncCursor(0, 0))

    fun queryChanges(
        after: MediaSyncCursor,
        limit: Int,
        types: Set<MediaType>,
    ): List<MediaStoreRow> = emptyList()

    fun queryManifest(
        afterId: Long?,
        limit: Int,
        types: Set<MediaType>,
    ): List<MediaManifestRow> = emptyList()

    fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow?

    fun loadThumbnail(mediaStoreId: Long, type: MediaType, width: Int, height: Int): ByteArray? = null

    fun openContent(mediaStoreId: Long, type: MediaType, offset: Long): InputStream? = null

    fun getContentType(mediaStoreId: Long, type: MediaType): String? = null
}

interface MediaPermissionGateway {
    fun missingPermissions(types: Set<MediaType>): Set<String>
}
