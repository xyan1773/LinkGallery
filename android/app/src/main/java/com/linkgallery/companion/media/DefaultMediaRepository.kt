package com.linkgallery.companion.media

import java.time.Instant

class DefaultMediaRepository(
    private val dataSource: MediaStoreDataSource,
    private val permissionGateway: MediaPermissionGateway,
    private val tokenCodec: OpaqueMediaTokenCodec = OpaqueMediaTokenCodec(),
) : MediaRepository {
    override suspend fun getPage(query: MediaQuery): MediaPageResult {
        val missingPermissions = permissionGateway.missingPermissions(query.types)
        if (missingPermissions.isNotEmpty()) {
            return MediaPageResult.PermissionDenied(missingPermissions)
        }

        val cursor = query.cursor?.let(tokenCodec::decodeCursor)
        if (query.cursor != null && cursor == null) {
            return MediaPageResult.InvalidCursor
        }

        val rows = dataSource.query(
            MediaStoreRequest(
                after = cursor,
                limit = query.limit + 1,
                types = query.types,
            ),
        )
        val hasNextPage = rows.size > query.limit
        val pageRows = rows.take(query.limit)
        return MediaPageResult.Success(
            MediaPage(
                items = pageRows.map(::toRecord),
                nextCursor = if (hasNextPage) {
                    pageRows.lastOrNull()?.let(tokenCodec::encodeCursor)
                } else {
                    null
                },
            ),
        )
    }

    override suspend fun getById(id: String): MediaItemResult {
        val locator = tokenCodec.decodeId(id) ?: return MediaItemResult.NotFound
        val missingPermissions = permissionGateway.missingPermissions(setOf(locator.type))
        if (missingPermissions.isNotEmpty()) {
            return MediaItemResult.PermissionDenied(missingPermissions)
        }
        val row = dataSource.find(locator.mediaStoreId, locator.type)
            ?: return MediaItemResult.NotFound
        return MediaItemResult.Found(toRecord(row))
    }

    override suspend fun getThumbnail(
        id: String,
        width: Int,
        height: Int,
    ): MediaThumbnailResult {
        val locator = tokenCodec.decodeId(id) ?: return MediaThumbnailResult.NotFound
        val missingPermissions = permissionGateway.missingPermissions(setOf(locator.type))
        if (missingPermissions.isNotEmpty()) {
            return MediaThumbnailResult.PermissionDenied(missingPermissions)
        }
        val bytes = dataSource.loadThumbnail(locator.mediaStoreId, locator.type, width, height)
            ?: return MediaThumbnailResult.NotFound
        return MediaThumbnailResult.Found(bytes)
    }

    private fun toRecord(row: MediaStoreRow): MediaRecord {
        val modifiedAt = Instant.ofEpochSecond(row.dateModifiedEpochSeconds)
        return MediaRecord(
            id = tokenCodec.encodeId(row),
            fileName = row.fileName,
            type = row.type,
            fileSize = row.fileSize,
            takenAt = row.dateTakenEpochMillis
                ?.takeIf { it > 0 }
                ?.let(Instant::ofEpochMilli)
                ?: modifiedAt,
            modifiedAt = modifiedAt,
            width = row.width,
            height = row.height,
            durationMilliseconds = row.durationMilliseconds,
            albumName = row.albumName,
            relativePath = row.relativePath,
        )
    }
}
