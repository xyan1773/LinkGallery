package com.linkgallery.companion.media

import java.io.InputStream

data class MediaStoreCursor(
    val sortTimestampEpochMillis: Long,
    val mediaStoreId: Long,
)

data class MediaStoreRequest(
    val after: MediaStoreCursor?,
    val limit: Int,
    val types: Set<MediaType>,
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
) {
    val sortTimestampEpochMillis: Long
        get() = dateTakenEpochMillis?.takeIf { it > 0 } ?: dateModifiedEpochSeconds * 1000
}

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

    fun count(types: Set<MediaType>): Int

    fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow?

    fun loadThumbnail(mediaStoreId: Long, type: MediaType, width: Int, height: Int): ByteArray? = null

    fun openContent(mediaStoreId: Long, type: MediaType, offset: Long): InputStream? = null

    fun getContentType(mediaStoreId: Long, type: MediaType): String? = null
}

interface MediaPermissionGateway {
    fun missingPermissions(types: Set<MediaType>): Set<String>
}
