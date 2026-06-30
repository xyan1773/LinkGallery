using System.Globalization;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using Microsoft.Data.Sqlite;

namespace LinkGallery.Infrastructure.Media;

public sealed class SqliteMediaIndex : IMediaIndex, IDisposable
{
    private const int SchemaVersion = 1;
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
                    file_name TEXT NOT NULL,
                    media_type INTEGER NOT NULL,
                    file_size INTEGER NOT NULL,
                    width INTEGER NULL,
                    height INTEGER NULL,
                    duration_ms INTEGER NULL,
                    taken_at TEXT NOT NULL,
                    modified_at TEXT NOT NULL,
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
                """, cancellationToken).ConfigureAwait(false);

            await using var migration = connection.CreateCommand();
            migration.CommandText = """
                INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                VALUES ($version, $appliedAt);
                """;
            migration.Parameters.AddWithValue("$version", SchemaVersion);
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
        command.CommandText = "SELECT COUNT(*) FROM media_items WHERE device_id = $deviceId;";
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
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO media_items(
                    device_id, remote_id, file_name, media_type, file_size, width, height, duration_ms,
                    taken_at, modified_at, album_name, relative_path, source_device, source_application,
                    is_edited_export, last_seen_at)
                VALUES(
                    $deviceId, $remoteId, $fileName, $mediaType, $fileSize, $width, $height, $duration,
                    $takenAt, $modifiedAt, $album, $path, $sourceDevice, $sourceApplication,
                    $isEdited, $lastSeenAt)
                ON CONFLICT(device_id, remote_id) DO UPDATE SET
                    file_name = excluded.file_name,
                    media_type = excluded.media_type,
                    file_size = excluded.file_size,
                    width = excluded.width,
                    height = excluded.height,
                    duration_ms = excluded.duration_ms,
                    taken_at = excluded.taken_at,
                    modified_at = excluded.modified_at,
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
                   is_edited_export
            FROM media_items
            WHERE ($deviceId IS NULL OR device_id = $deviceId)
              AND ($pattern IS NULL
                   OR file_name LIKE $pattern ESCAPE '\'
                   OR album_name LIKE $pattern ESCAPE '\'
                   OR relative_path LIKE $pattern ESCAPE '\')
            ORDER BY taken_at DESC, remote_id DESC
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

    private static void AddItemParameters(
        SqliteCommand command,
        MediaItem item,
        DateTimeOffset seenAt)
    {
        command.Parameters.AddWithValue("$deviceId", item.DeviceId);
        command.Parameters.AddWithValue("$remoteId", item.RemoteId);
        command.Parameters.AddWithValue("$fileName", item.FileName);
        command.Parameters.AddWithValue("$mediaType", (int)item.Type);
        command.Parameters.AddWithValue("$fileSize", item.FileSize);
        command.Parameters.AddWithValue("$width", Db(item.Width));
        command.Parameters.AddWithValue("$height", Db(item.Height));
        command.Parameters.AddWithValue("$duration", Db(item.DurationMilliseconds));
        command.Parameters.AddWithValue("$takenAt", Format(item.TakenAt));
        command.Parameters.AddWithValue("$modifiedAt", Format(item.ModifiedAt));
        command.Parameters.AddWithValue("$album", Db(item.AlbumName));
        command.Parameters.AddWithValue("$path", Db(item.RelativePath));
        command.Parameters.AddWithValue("$sourceDevice", Db(item.SourceDevice));
        command.Parameters.AddWithValue("$sourceApplication", Db(item.SourceApplication));
        command.Parameters.AddWithValue("$isEdited", item.IsEditedExport);
        command.Parameters.AddWithValue("$lastSeenAt", Format(seenAt));
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
    };

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
