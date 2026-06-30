package com.linkgallery.companion.media

interface MediaRepository {
    suspend fun getPage(query: MediaQuery): MediaPage
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

