package com.linkgallery.companion.media

import java.time.Instant
import java.security.MessageDigest

class DefaultMediaRepository(
    private val dataSource: MediaStoreDataSource,
    private val permissionGateway: MediaPermissionGateway,
    private val tokenCodec: OpaqueMediaTokenCodec = OpaqueMediaTokenCodec(),
) : MediaRepository {
    override suspend fun getSyncState(): MediaSyncStateResult {
        val types = MediaType.entries.toSet()
        val missingPermissions = permissionGateway.missingPermissions(types)
        if (missingPermissions.isNotEmpty()) {
            return MediaSyncStateResult.PermissionDenied(missingPermissions)
        }
        val state = dataSource.libraryState()
        return MediaSyncStateResult.Success(
            MediaSyncState(
                state.libraryVersion,
                tokenCodec.encodeSyncCursor(state.latestCursor),
                dataSource.count(types),
            ),
        )
    }

    override suspend fun getChanges(cursor: String?, limit: Int): MediaChangesResult {
        val types = MediaType.entries.toSet()
        val missingPermissions = permissionGateway.missingPermissions(types)
        if (missingPermissions.isNotEmpty()) {
            return MediaChangesResult.PermissionDenied(missingPermissions)
        }
        val after = cursor?.let(tokenCodec::decodeSyncCursor) ?: MediaSyncCursor(0, 0)
        if (cursor != null && tokenCodec.decodeSyncCursor(cursor) == null) {
            return MediaChangesResult.InvalidCursor
        }
        val state = dataSource.libraryState()
        val rows = dataSource.queryChanges(after, limit + 1, types)
        val hasMore = rows.size > limit
        val pageRows = rows.take(limit)
        val nextValue = pageRows.lastOrNull()?.let { row ->
            MediaSyncCursor(
                row.generation ?: row.dateModifiedEpochSeconds,
                row.mediaStoreId,
            )
        } ?: after
        return MediaChangesResult.Success(
            MediaChanges(
                libraryVersion = state.libraryVersion,
                fromCursor = cursor,
                nextCursor = tokenCodec.encodeSyncCursor(nextValue),
                latestCursor = tokenCodec.encodeSyncCursor(state.latestCursor),
                hasMore = hasMore,
                upserts = pageRows.map(::toRecord),
            ),
        )
    }

    override suspend fun getManifest(cursor: String?, limit: Int): MediaManifestResult {
        val types = MediaType.entries.toSet()
        val missingPermissions = permissionGateway.missingPermissions(types)
        if (missingPermissions.isNotEmpty()) {
            return MediaManifestResult.PermissionDenied(missingPermissions)
        }
        val afterId = cursor?.let(tokenCodec::decodeManifestCursor)
        if (cursor != null && afterId == null) {
            return MediaManifestResult.InvalidCursor
        }
        val state = dataSource.libraryState()
        val rows = dataSource.queryManifest(afterId, limit + 1, types)
        val hasMore = rows.size > limit
        val pageRows = rows.take(limit)
        return MediaManifestResult.Success(
            MediaManifestPage(
                libraryVersion = state.libraryVersion,
                items = pageRows.map { row ->
                    MediaManifestEntry(
                        tokenCodec.encodeId(row.type, row.mediaStoreId),
                        row.generation,
                    )
                },
                nextCursor = if (hasMore) {
                    pageRows.lastOrNull()?.let { tokenCodec.encodeManifestCursor(it.mediaStoreId) }
                } else {
                    null
                },
                hasMore = hasMore,
            ),
        )
    }

    override suspend fun getPage(query: MediaQuery): MediaPageResult {
        val missingPermissions = permissionGateway.missingPermissions(query.types)
        if (missingPermissions.isNotEmpty()) {
            return MediaPageResult.PermissionDenied(missingPermissions)
        }

        val scope = OpaqueMediaTokenCodec.cursorScope(query.albumId, query.types)
        val cursor = query.before ?: query.cursor?.let { tokenCodec.decodeCursor(it, scope) }
        if (query.cursor != null && cursor == null) {
            return MediaPageResult.InvalidCursor
        }

        val total = dataSource.count(query.types, query.albumId)
        val rows = dataSource.query(
            MediaStoreRequest(
                after = cursor,
                limit = query.limit + 1,
                types = query.types,
                albumId = query.albumId,
            ),
        )
        val hasNextPage = rows.size > query.limit
        val pageRows = rows.take(query.limit)
        return MediaPageResult.Success(
            MediaPage(
                items = pageRows.map(::toRecord),
                nextCursor = if (hasNextPage) {
                    pageRows.lastOrNull()?.let { tokenCodec.encodeCursor(it, scope) }
                } else {
                    null
                },
                hasMore = hasNextPage,
                total = total,
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
        val row = dataSource.find(locator.mediaStoreId, locator.type)
            ?: return MediaThumbnailResult.NotFound
        val bytes = dataSource.loadThumbnail(locator.mediaStoreId, locator.type, width, height)
            ?: return MediaThumbnailResult.NotFound
        return MediaThumbnailResult.Found(bytes, thumbnailEntityTag(row, width, height))
    }

    override suspend fun getContent(id: String): MediaContentResult {
        val locator = tokenCodec.decodeId(id) ?: return MediaContentResult.NotFound
        val missingPermissions = permissionGateway.missingPermissions(setOf(locator.type))
        if (missingPermissions.isNotEmpty()) {
            return MediaContentResult.PermissionDenied(missingPermissions)
        }

        val row = dataSource.find(locator.mediaStoreId, locator.type)
            ?: return MediaContentResult.NotFound
        val contentType = dataSource.getContentType(locator.mediaStoreId, locator.type)
            ?: when (locator.type) {
                MediaType.IMAGE -> "image/*"
                MediaType.VIDEO -> "video/*"
            }
        val entity = dataSource.openContent(locator.mediaStoreId, locator.type, 0)
            ?.use(::computeEntity)
            ?: return MediaContentResult.NotFound
        if (entity.length != row.fileSize) {
            return MediaContentResult.NotFound
        }
        return MediaContentResult.Found(
            MediaContent(row.fileSize, contentType, entity.entityTag) { offset ->
                dataSource.openContent(locator.mediaStoreId, locator.type, offset)
            },
        )
    }

    private fun computeEntity(input: java.io.InputStream): ContentEntity {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
        var length = 0L
        while (true) {
            val read = input.read(buffer)
            if (read < 0) break
            digest.update(buffer, 0, read)
            length += read
        }
        val hash = digest.digest().joinToString("") { byte -> "%02x".format(byte) }
        return ContentEntity(length, "\"sha256-$hash\"")
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
            albumId = row.albumId,
            albumName = row.albumName,
            relativePath = row.relativePath,
            generation = row.generation,
            thumbnailUrl = "/api/v1/media/${tokenCodec.encodeId(row)}/thumbnail?size=256",
        )
    }

    private fun thumbnailEntityTag(row: MediaStoreRow, width: Int, height: Int): String =
        "\"thumb-${row.type.name.lowercase()}-${row.mediaStoreId}-" +
            "${row.generation ?: row.dateModifiedEpochSeconds}-${row.fileSize}-${width}x$height\""

    private data class ContentEntity(
        val length: Long,
        val entityTag: String,
    )
}
