package com.linkgallery.companion.media

interface MediaRepository {
    suspend fun getPage(query: MediaQuery): MediaPageResult

    suspend fun getById(id: String): MediaItemResult

    suspend fun getThumbnail(id: String, width: Int, height: Int): MediaThumbnailResult =
        MediaThumbnailResult.NotFound
}

data class MediaQuery(
    val cursor: String? = null,
    val limit: Int = 100,
    val types: Set<MediaType> = MediaType.entries.toSet(),
) {
    init {
        require(limit in 1..200) { "Page size must be between 1 and 200." }
        require(types.isNotEmpty()) { "At least one media type is required." }
    }
}

data class MediaPage(
    val items: List<MediaRecord>,
    val nextCursor: String?,
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
