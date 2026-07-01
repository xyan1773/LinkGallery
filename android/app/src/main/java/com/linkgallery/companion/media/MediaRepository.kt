package com.linkgallery.companion.media

import java.io.InputStream

interface MediaRepository {
    suspend fun getPage(query: MediaQuery): MediaPageResult

    suspend fun getById(id: String): MediaItemResult

    suspend fun getThumbnail(id: String, width: Int, height: Int): MediaThumbnailResult =
        MediaThumbnailResult.NotFound

    suspend fun getContent(id: String): MediaContentResult =
        MediaContentResult.NotFound
}

data class MediaQuery(
    val cursor: String? = null,
    val before: MediaStoreCursor? = null,
    val limit: Int = 100,
    val types: Set<MediaType> = MediaType.entries.toSet(),
) {
    init {
        require(cursor == null || before == null) { "Use either cursor or before, not both." }
        require(limit in 1..200) { "Page size must be between 1 and 200." }
        require(types.isNotEmpty()) { "At least one media type is required." }
    }
}

data class MediaPage(
    val items: List<MediaRecord>,
    val nextCursor: String?,
    val hasMore: Boolean,
    val total: Int,
)

sealed interface MediaPageResult {
    data class Success(val page: MediaPage) : MediaPageResult

    data class PermissionDenied(val requiredPermissions: Set<String>) : MediaPageResult

    data object InvalidCursor : MediaPageResult
}

sealed interface MediaItemResult {
    data class Found(val item: MediaRecord) : MediaItemResult

    data class PermissionDenied(val requiredPermissions: Set<String>) : MediaItemResult

    data object NotFound : MediaItemResult
}

sealed interface MediaThumbnailResult {
    data class Found(val jpeg: ByteArray) : MediaThumbnailResult

    data class PermissionDenied(val requiredPermissions: Set<String>) : MediaThumbnailResult

    data object NotFound : MediaThumbnailResult
}

class MediaContent(
    val length: Long,
    val contentType: String,
    val entityTag: String? = null,
    private val openAt: (Long) -> InputStream?,
) {
    init {
        require(length >= 0) { "Content length cannot be negative." }
        require(contentType.isNotBlank()) { "Content type is required." }
    }

    fun open(offset: Long): InputStream? {
        require(offset in 0..length) { "Offset must be within the content." }
        return openAt(offset)
    }
}

sealed interface MediaContentResult {
    data class Found(val content: MediaContent) : MediaContentResult

    data class PermissionDenied(val requiredPermissions: Set<String>) : MediaContentResult

    data object NotFound : MediaContentResult
}
