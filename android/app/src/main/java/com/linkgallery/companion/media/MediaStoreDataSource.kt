package com.linkgallery.companion.media

data class MediaStoreCursor(
    val modifiedAtEpochSeconds: Long,
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
)

/**
 * Injectable, read-only boundary around Android's MediaStore.
 *
 * Implementations must return rows ordered by modified time and ID, both descending.
 */
interface MediaStoreDataSource {
    fun query(request: MediaStoreRequest): List<MediaStoreRow>

    fun find(mediaStoreId: Long, type: MediaType): MediaStoreRow?
}

interface MediaPermissionGateway {
    fun missingPermissions(types: Set<MediaType>): Set<String>
}
