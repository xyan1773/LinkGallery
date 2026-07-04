using System.Globalization;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using Microsoft.Data.Sqlite;

namespace LinkGallery.Infrastructure.Media;

public sealed class SqliteMediaIndex : IMediaIndex, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteMediaIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS devices (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    platform TEXT NOT NULL,
                    model TEXT NULL,
                    battery_percent INTEGER NULL,
                    media_count INTEGER NOT NULL,
                    address TEXT NULL,
                    is_online INTEGER NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS media_items (
                    device_id TEXT NOT NULL,
                    remote_id TEXT NOT NULL,
                    media_key TEXT NOT NULL,
                    media_id INTEGER NULL,
                    file_name TEXT NOT NULL,
                    media_type INTEGER NOT NULL,
                    mime_type TEXT NULL,
                    file_size INTEGER NOT NULL,
                    width INTEGER NULL,
                    height INTEGER NULL,
                    duration_ms INTEGER NULL,
                    taken_at TEXT NOT NULL,
                    modified_at TEXT NOT NULL,
                    sort_time INTEGER NOT NULL,
                    date_taken INTEGER NOT NULL,
                    date_modified INTEGER NOT NULL,
                    generation INTEGER NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL,
                    album_id TEXT NOT NULL,
                    album_name TEXT NULL,
                    relative_path TEXT NULL,
                    source_device TEXT NULL,
                    source_application TEXT NULL,
                    is_edited_export INTEGER NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    PRIMARY KEY (device_id, remote_id),
                    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_media_items_timeline
                    ON media_items(device_id, taken_at DESC, remote_id DESC);
                CREATE INDEX IF NOT EXISTS ix_media_items_file_name
                    ON media_items(file_name);
                CREATE TABLE IF NOT EXISTS sync_cursors (
                    device_id TEXT NOT NULL PRIMARY KEY,
                    head_remote_id TEXT NULL,
                    head_modified_at TEXT NULL,
                    last_completed_at TEXT NOT NULL,
                    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS device_sync_state (
                    device_id TEXT NOT NULL PRIMARY KEY,
                    library_version TEXT NULL,
                    sync_cursor TEXT NULL,
                    latest_known_cursor TEXT NULL,
                    full_index_completed INTEGER NOT NULL DEFAULT 0,
                    last_sync_at INTEGER NULL,
                    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS thumbnail_cache (
                    device_id TEXT NOT NULL,
                    media_key TEXT NOT NULL,
                    generation INTEGER NOT NULL,
                    thumbnail_size INTEGER NOT NULL,
                    local_path TEXT NOT NULL,
                    file_size INTEGER NULL,
                    last_accessed_at INTEGER NULL,
                    PRIMARY KEY (device_id, media_key, generation, thumbnail_size)
                );
                CREATE TABLE IF NOT EXISTS albums (
                    device_id TEXT NOT NULL,
                    album_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    relative_path TEXT NULL,
                    cover_media_id TEXT NULL,
                    media_count INTEGER NOT NULL,
                    photo_count INTEGER NOT NULL,
                    video_count INTEGER NOT NULL,
                    latest_sort_time INTEGER NOT NULL,
                    last_synced_at INTEGER NOT NULL,
                    PRIMARY KEY (device_id, album_id),
                    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_albums_device_latest
                    ON albums(device_id, latest_sort_time DESC);
                """, cancellationToken).ConfigureAwait(false);

            await ApplySchemaV2Async(connection, cancellationToken).ConfigureAwait(false);

            await using var migration = connection.CreateCommand();
            migration.CommandText = """
                INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                VALUES (1, $appliedAt), (2, $appliedAt);
                """;
            migration.Parameters.AddWithValue("$appliedAt", Format(DateTimeOffset.UtcNow));
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    internal async Task UpsertDeviceAsync(Device device, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices(
                id, name, platform, model, battery_percent, media_count, address, is_online, last_seen_at)
            VALUES(
                $id, $name, $platform, $model, $battery, $mediaCount, $address, $isOnline, $lastSeenAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                platform = excluded.platform,
                model = excluded.model,
                battery_percent = excluded.battery_percent,
                media_count = excluded.media_count,
                address = excluded.address,
                is_online = excluded.is_online,
                last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$id", device.Id);
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$platform", device.Platform);
        command.Parameters.AddWithValue("$model", Db(device.Model));
        command.Parameters.AddWithValue("$battery", Db(device.BatteryPercent));
        command.Parameters.AddWithValue("$mediaCount", device.MediaCount);
        command.Parameters.AddWithValue("$address", Db(device.Address?.AbsoluteUri));
        command.Parameters.AddWithValue("$isOnline", device.IsOnline);
        command.Parameters.AddWithValue("$lastSeenAt", Format(device.LastSeenAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SyncCheckpoint?> GetCheckpointAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT head_remote_id, head_modified_at
            FROM sync_cursors
            WHERE device_id = $deviceId;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return new SyncCheckpoint(reader.GetString(0), Parse(reader.GetString(1)));
    }

    internal async Task<int> CountAsync(string deviceId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM media_items
            WHERE device_id = $deviceId AND is_deleted = 0;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    internal async Task UpsertItemsAsync(
        IReadOnlyList<MediaItem> items,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            await UpsertItemAsync(
                connection,
                (SqliteTransaction)transaction,
                item,
                seenAt,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var deviceId in items.Select(static item => item.DeviceId).Distinct(StringComparer.Ordinal))
        {
            await RefreshAlbumsAsync(connection, (SqliteTransaction)transaction, deviceId, seenAt, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DeviceSyncState?> GetDeviceSyncStateAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT library_version, sync_cursor, latest_known_cursor, full_index_completed, last_sync_at
            FROM device_sync_state
            WHERE device_id = $deviceId;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DeviceSyncState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3),
            reader.IsDBNull(4)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
    }

    internal async Task SaveDeviceSyncStateAsync(
        string deviceId,
        RemoteMediaSyncState state,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_sync_state(
                device_id, library_version, sync_cursor, latest_known_cursor,
                full_index_completed, last_sync_at)
            VALUES($deviceId, $version, $cursor, $cursor, 1, $syncedAt)
            ON CONFLICT(device_id) DO UPDATE SET
                library_version = excluded.library_version,
                sync_cursor = excluded.sync_cursor,
                latest_known_cursor = excluded.latest_known_cursor,
                full_index_completed = 1,
                last_sync_at = excluded.last_sync_at;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$version", state.LibraryVersion);
        command.Parameters.AddWithValue("$cursor", state.LatestCursor);
        command.Parameters.AddWithValue("$syncedAt", syncedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> ApplyChangePageAsync(
        string deviceId,
        RemoteMediaChanges page,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in page.Upserts)
        {
            if (!string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Incremental page contains media for another device.");
            }
            await UpsertItemAsync(connection, transaction, item, syncedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        var deleted = 0;
        foreach (var remoteId in page.Deletes.Distinct(StringComparer.Ordinal))
        {
            await using var tombstone = connection.CreateCommand();
            tombstone.Transaction = transaction;
            tombstone.CommandText = """
                UPDATE media_items
                SET is_deleted = 1, updated_at = $updatedAt
                WHERE device_id = $deviceId
                  AND remote_id = $remoteId
                  AND is_deleted = 0;
                """;
            tombstone.Parameters.AddWithValue("$updatedAt", syncedAt.ToUnixTimeMilliseconds());
            tombstone.Parameters.AddWithValue("$deviceId", deviceId);
            tombstone.Parameters.AddWithValue("$remoteId", remoteId);
            deleted += await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO device_sync_state(
                device_id, library_version, sync_cursor, latest_known_cursor,
                full_index_completed, last_sync_at)
            VALUES($deviceId, $version, $cursor, $latestCursor, 1, $syncedAt)
            ON CONFLICT(device_id) DO UPDATE SET
                library_version = excluded.library_version,
                sync_cursor = excluded.sync_cursor,
                latest_known_cursor = excluded.latest_known_cursor,
                full_index_completed = 1,
                last_sync_at = excluded.last_sync_at;
            """;
        state.Parameters.AddWithValue("$deviceId", deviceId);
        state.Parameters.AddWithValue("$version", page.LibraryVersion);
        state.Parameters.AddWithValue("$cursor", page.NextCursor);
        state.Parameters.AddWithValue("$latestCursor", page.LatestCursor);
        state.Parameters.AddWithValue("$syncedAt", syncedAt.ToUnixTimeMilliseconds());
        await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await RefreshAlbumsAsync(connection, transaction, deviceId, syncedAt, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    internal async Task<int> ReconcileManifestAsync(
        string deviceId,
        IReadOnlyList<RemoteMediaManifestEntry> manifest,
        RemoteMediaSyncState state,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                DROP TABLE IF EXISTS current_media_manifest;
                CREATE TEMP TABLE current_media_manifest(
                    remote_id TEXT NOT NULL PRIMARY KEY,
                    generation INTEGER NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in manifest)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO current_media_manifest(remote_id, generation)
                VALUES($remoteId, $generation);
                """;
            insert.Parameters.AddWithValue("$remoteId", entry.Id);
            insert.Parameters.AddWithValue("$generation", Db(entry.Generation));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            UPDATE media_items
            SET is_deleted = 1, updated_at = $updatedAt
            WHERE device_id = $deviceId
              AND is_deleted = 0
              AND NOT EXISTS(
                  SELECT 1
                  FROM current_media_manifest manifest
                  WHERE manifest.remote_id = media_items.remote_id
              );
            """;
        delete.Parameters.AddWithValue("$updatedAt", syncedAt.ToUnixTimeMilliseconds());
        delete.Parameters.AddWithValue("$deviceId", deviceId);
        var deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var restore = connection.CreateCommand();
        restore.Transaction = transaction;
        restore.CommandText = """
            UPDATE media_items
            SET is_deleted = 0, updated_at = $updatedAt
            WHERE device_id = $deviceId
              AND is_deleted = 1
              AND EXISTS(
                  SELECT 1
                  FROM current_media_manifest manifest
                  WHERE manifest.remote_id = media_items.remote_id
              );
            """;
        restore.Parameters.AddWithValue("$updatedAt", syncedAt.ToUnixTimeMilliseconds());
        restore.Parameters.AddWithValue("$deviceId", deviceId);
        await restore.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var syncState = connection.CreateCommand();
        syncState.Transaction = transaction;
        syncState.CommandText = """
            INSERT INTO device_sync_state(
                device_id, library_version, sync_cursor, latest_known_cursor,
                full_index_completed, last_sync_at)
            VALUES($deviceId, $version, $cursor, $cursor, 1, $syncedAt)
            ON CONFLICT(device_id) DO UPDATE SET
                library_version = excluded.library_version,
                sync_cursor = excluded.sync_cursor,
                latest_known_cursor = excluded.latest_known_cursor,
                full_index_completed = 1,
                last_sync_at = excluded.last_sync_at;
            """;
        syncState.Parameters.AddWithValue("$deviceId", deviceId);
        syncState.Parameters.AddWithValue("$version", state.LibraryVersion);
        syncState.Parameters.AddWithValue("$cursor", state.LatestCursor);
        syncState.Parameters.AddWithValue("$syncedAt", syncedAt.ToUnixTimeMilliseconds());
        await syncState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAlbumsAsync(connection, transaction, deviceId, syncedAt, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    internal async Task RecordThumbnailCacheAccessAsync(
        ThumbnailCacheKey key,
        string localPath,
        long fileSize,
        DateTimeOffset accessedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thumbnail_cache(
                device_id, media_key, generation, thumbnail_size,
                local_path, file_size, last_accessed_at)
            VALUES(
                $deviceId, $mediaKey, $generation, $thumbnailSize,
                $localPath, $fileSize, $lastAccessedAt)
            ON CONFLICT(device_id, media_key, generation, thumbnail_size) DO UPDATE SET
                local_path = excluded.local_path,
                file_size = excluded.file_size,
                last_accessed_at = excluded.last_accessed_at;
            """;
        command.Parameters.AddWithValue("$deviceId", key.DeviceId);
        command.Parameters.AddWithValue("$mediaKey", key.RemoteId);
        command.Parameters.AddWithValue("$generation", key.Generation);
        command.Parameters.AddWithValue("$thumbnailSize", Math.Max(key.Width, key.Height));
        command.Parameters.AddWithValue("$localPath", Path.GetFullPath(localPath));
        command.Parameters.AddWithValue("$fileSize", fileSize);
        command.Parameters.AddWithValue("$lastAccessedAt", accessedAt.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ClearThumbnailCacheMetadataAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM thumbnail_cache;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> CompleteAsync(
        string deviceId,
        SyncCheckpoint? head,
        DateTimeOffset completedAt,
        bool removeItemsNotSeen,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        if (removeItemsNotSeen)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM media_items
                WHERE device_id = $deviceId AND last_seen_at <> $completedAt;
                """;
            delete.Parameters.AddWithValue("$deviceId", deviceId);
            delete.Parameters.AddWithValue("$completedAt", Format(completedAt));
            removed = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var cursor = connection.CreateCommand();
        cursor.Transaction = (SqliteTransaction)transaction;
        cursor.CommandText = """
            INSERT INTO sync_cursors(device_id, head_remote_id, head_modified_at, last_completed_at)
            VALUES($deviceId, $headId, $headModifiedAt, $completedAt)
            ON CONFLICT(device_id) DO UPDATE SET
                head_remote_id = excluded.head_remote_id,
                head_modified_at = excluded.head_modified_at,
                last_completed_at = excluded.last_completed_at;
            """;
        cursor.Parameters.AddWithValue("$deviceId", deviceId);
        cursor.Parameters.AddWithValue("$headId", Db(head?.RemoteId));
        cursor.Parameters.AddWithValue("$headModifiedAt", Db(head is null ? null : Format(head.ModifiedAt)));
        cursor.Parameters.AddWithValue("$completedAt", Format(completedAt));
        await cursor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAlbumsAsync(
            connection,
            (SqliteTransaction)transaction,
            deviceId,
            completedAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(
        string? deviceId,
        string? searchText,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, remote_id, file_name, media_type, file_size, width, height, duration_ms,
                   taken_at, modified_at, album_name, relative_path, source_device, source_application,
                   is_edited_export, mime_type, generation, album_id
            FROM media_items
            WHERE ($deviceId IS NULL OR device_id = $deviceId)
              AND is_deleted = 0
              AND ($pattern IS NULL
                   OR file_name LIKE $pattern ESCAPE '\'
                   OR album_name LIKE $pattern ESCAPE '\'
                   OR relative_path LIKE $pattern ESCAPE '\')
            ORDER BY sort_time DESC, remote_id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$deviceId", Db(deviceId));
        command.Parameters.AddWithValue(
            "$pattern",
            Db(string.IsNullOrWhiteSpace(searchText) ? null : $"%{EscapeLike(searchText.Trim())}%"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<MediaItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<MediaAlbum>> GetAlbumsAsync(
        string deviceId,
        string? searchText,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, album_id, display_name, relative_path, cover_media_id,
                   media_count, photo_count, video_count, latest_sort_time
            FROM albums
            WHERE device_id = $deviceId
              AND ($pattern IS NULL
                   OR display_name LIKE $pattern ESCAPE '\'
                   OR relative_path LIKE $pattern ESCAPE '\')
            ORDER BY latest_sort_time DESC, album_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue(
            "$pattern",
            Db(string.IsNullOrWhiteSpace(searchText) ? null : $"%{EscapeLike(searchText.Trim())}%"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var albums = new List<MediaAlbum>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            albums.Add(new MediaAlbum(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        }
        return albums;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _migrationLock.Dispose();
        _disposed = true;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplySchemaV2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["media_key"] = "TEXT",
            ["media_id"] = "INTEGER",
            ["mime_type"] = "TEXT",
            ["sort_time"] = "INTEGER",
            ["date_taken"] = "INTEGER",
            ["date_modified"] = "INTEGER",
            ["generation"] = "INTEGER",
            ["is_deleted"] = "INTEGER NOT NULL DEFAULT 0",
            ["updated_at"] = "INTEGER",
            ["album_id"] = "TEXT",
        };
        foreach (var (name, definition) in columns)
        {
            if (await ColumnExistsAsync(connection, "media_items", name, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            await ExecuteAsync(
                connection,
                $"ALTER TABLE media_items ADD COLUMN {name} {definition};",
                cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, """
            UPDATE media_items
            SET media_key = COALESCE(media_key, remote_id),
                mime_type = COALESCE(mime_type, CASE media_type WHEN 0 THEN 'image/*' ELSE 'video/*' END),
                sort_time = COALESCE(sort_time, unixepoch(taken_at) * 1000),
                date_taken = COALESCE(date_taken, unixepoch(taken_at) * 1000),
                date_modified = COALESCE(date_modified, unixepoch(modified_at) * 1000),
                updated_at = COALESCE(updated_at, unixepoch(last_seen_at) * 1000),
                album_id = COALESCE(
                    album_id,
                    lower(trim(replace(COALESCE(relative_path, '__unsorted'), '\', '/'), '/')));
            DROP INDEX IF EXISTS ix_media_items_timeline;
            CREATE INDEX IF NOT EXISTS ix_media_items_timeline
                ON media_items(device_id, sort_time DESC, remote_id DESC);
            CREATE INDEX IF NOT EXISTS ix_media_items_album_timeline
                ON media_items(device_id, album_id, sort_time DESC, remote_id DESC);
            DELETE FROM albums;
            INSERT INTO albums(
                device_id, album_id, display_name, relative_path, cover_media_id,
                media_count, photo_count, video_count, latest_sort_time, last_synced_at)
            SELECT m.device_id,
                   m.album_id,
                   COALESCE(MAX(m.album_name), 'Unsorted'),
                   MAX(m.relative_path),
                   (SELECT cover.remote_id
                    FROM media_items cover
                    WHERE cover.device_id = m.device_id
                      AND cover.album_id = m.album_id
                      AND cover.is_deleted = 0
                    ORDER BY cover.sort_time DESC, cover.remote_id DESC
                    LIMIT 1),
                   COUNT(*),
                   SUM(CASE WHEN m.media_type = 0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN m.media_type = 1 THEN 1 ELSE 0 END),
                   MAX(m.sort_time),
                   MAX(m.updated_at)
            FROM media_items m
            WHERE m.is_deleted = 0
            GROUP BY m.device_id, m.album_id;
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task RefreshAlbumsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM albums WHERE device_id = $deviceId;";
        delete.Parameters.AddWithValue("$deviceId", deviceId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO albums(
                device_id, album_id, display_name, relative_path, cover_media_id,
                media_count, photo_count, video_count, latest_sort_time, last_synced_at)
            SELECT m.device_id,
                   m.album_id,
                   COALESCE(MAX(m.album_name), 'Unsorted'),
                   MAX(m.relative_path),
                   (SELECT cover.remote_id
                    FROM media_items cover
                    WHERE cover.device_id = m.device_id
                      AND cover.album_id = m.album_id
                      AND cover.is_deleted = 0
                    ORDER BY cover.sort_time DESC, cover.remote_id DESC
                    LIMIT 1),
                   COUNT(*),
                   SUM(CASE WHEN m.media_type = 0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN m.media_type = 1 THEN 1 ELSE 0 END),
                   MAX(m.sort_time),
                   $syncedAt
            FROM media_items m
            WHERE m.device_id = $deviceId AND m.is_deleted = 0
            GROUP BY m.device_id, m.album_id;
            """;
        insert.Parameters.AddWithValue("$deviceId", deviceId);
        insert.Parameters.AddWithValue("$syncedAt", syncedAt.ToUnixTimeMilliseconds());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddItemParameters(
        SqliteCommand command,
        MediaItem item,
        DateTimeOffset seenAt)
    {
        command.Parameters.AddWithValue("$deviceId", item.DeviceId);
        command.Parameters.AddWithValue("$remoteId", item.RemoteId);
        command.Parameters.AddWithValue("$fileName", item.FileName);
        command.Parameters.AddWithValue("$mediaType", (int)item.Type);
        command.Parameters.AddWithValue("$mimeType", Db(item.MimeType ?? DefaultMimeType(item.Type)));
        command.Parameters.AddWithValue("$fileSize", item.FileSize);
        command.Parameters.AddWithValue("$width", Db(item.Width));
        command.Parameters.AddWithValue("$height", Db(item.Height));
        command.Parameters.AddWithValue("$duration", Db(item.DurationMilliseconds));
        command.Parameters.AddWithValue("$takenAt", Format(item.TakenAt));
        command.Parameters.AddWithValue("$modifiedAt", Format(item.ModifiedAt));
        command.Parameters.AddWithValue("$sortTime", item.TakenAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$dateTaken", item.TakenAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$dateModified", item.ModifiedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$generation", Db(item.Generation));
        command.Parameters.AddWithValue("$updatedAt", seenAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$albumId", item.AlbumId ?? NormalizeAlbumId(item.RelativePath));
        command.Parameters.AddWithValue("$album", Db(item.AlbumName));
        command.Parameters.AddWithValue("$path", Db(item.RelativePath));
        command.Parameters.AddWithValue("$sourceDevice", Db(item.SourceDevice));
        command.Parameters.AddWithValue("$sourceApplication", Db(item.SourceApplication));
        command.Parameters.AddWithValue("$isEdited", item.IsEditedExport);
        command.Parameters.AddWithValue("$lastSeenAt", Format(seenAt));
    }

    private static async Task UpsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MediaItem item,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO media_items(
                device_id, remote_id, media_key, media_id, file_name, media_type, mime_type,
                file_size, width, height, duration_ms, taken_at, modified_at, sort_time,
                date_taken, date_modified, generation, is_deleted, updated_at, album_id,
                album_name, relative_path, source_device, source_application, is_edited_export,
                last_seen_at)
            VALUES(
                $deviceId, $remoteId, $remoteId, NULL, $fileName, $mediaType, $mimeType,
                $fileSize, $width, $height, $duration, $takenAt, $modifiedAt, $sortTime,
                $dateTaken, $dateModified, $generation, 0, $updatedAt, $albumId,
                $album, $path, $sourceDevice, $sourceApplication, $isEdited, $lastSeenAt)
            ON CONFLICT(device_id, remote_id) DO UPDATE SET
                media_key = excluded.media_key,
                file_name = excluded.file_name,
                media_type = excluded.media_type,
                mime_type = excluded.mime_type,
                file_size = excluded.file_size,
                width = excluded.width,
                height = excluded.height,
                duration_ms = excluded.duration_ms,
                taken_at = excluded.taken_at,
                modified_at = excluded.modified_at,
                sort_time = excluded.sort_time,
                date_taken = excluded.date_taken,
                date_modified = excluded.date_modified,
                generation = excluded.generation,
                is_deleted = 0,
                updated_at = excluded.updated_at,
                album_id = excluded.album_id,
                album_name = excluded.album_name,
                relative_path = excluded.relative_path,
                source_device = excluded.source_device,
                source_application = excluded.source_application,
                is_edited_export = excluded.is_edited_export,
                last_seen_at = excluded.last_seen_at;
            """;
        AddItemParameters(command, item, seenAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MediaItem ReadItem(SqliteDataReader reader) => new()
    {
        DeviceId = reader.GetString(0),
        RemoteId = reader.GetString(1),
        FileName = reader.GetString(2),
        Type = (MediaType)reader.GetInt32(3),
        FileSize = reader.GetInt64(4),
        Width = NullableInt32(reader, 5),
        Height = NullableInt32(reader, 6),
        DurationMilliseconds = NullableInt64(reader, 7),
        TakenAt = Parse(reader.GetString(8)),
        ModifiedAt = Parse(reader.GetString(9)),
        AlbumName = NullableString(reader, 10),
        RelativePath = NullableString(reader, 11),
        SourceDevice = NullableString(reader, 12),
        SourceApplication = NullableString(reader, 13),
        IsEditedExport = reader.GetBoolean(14),
        MimeType = NullableString(reader, 15),
        Generation = NullableInt64(reader, 16),
        AlbumId = NullableString(reader, 17),
    };

    private static string NormalizeAlbumId(string? relativePath)
    {
        var normalized = relativePath?
            .Replace('\\', '/')
            .Trim()
            .Trim('/')
            .ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "__unsorted" : normalized;
    }

    private static string DefaultMimeType(MediaType type) =>
        type == MediaType.Image ? "image/*" : "video/*";

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}

internal sealed record SyncCheckpoint(string RemoteId, DateTimeOffset ModifiedAt);

internal sealed record DeviceSyncState(
    string? LibraryVersion,
    string? SyncCursor,
    string? LatestKnownCursor,
    bool FullIndexCompleted,
    DateTimeOffset? LastSyncAt);
