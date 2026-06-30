package com.linkgallery.companion.media

import java.time.Instant

data class MediaRecord(
    val id: String,
    val fileName: String,
    val type: MediaType,
    val fileSize: Long,
    val takenAt: Instant,
    val modifiedAt: Instant,
    val width: Int? = null,
    val height: Int? = null,
    val durationMilliseconds: Long? = null,
    val albumName: String? = null,
    val relativePath: String? = null,
    val sourceDevice: String? = null,
    val sourceApplication: String? = null,
    val isEditedExport: Boolean = false,
)

enum class MediaType {
    IMAGE,
    VIDEO,
}

