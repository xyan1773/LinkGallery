using System.Globalization;
using LinkGallery.Application.Devices;
using LinkGallery.Domain.Devices;
using Microsoft.Data.Sqlite;

namespace LinkGallery.Infrastructure.Devices;

public sealed class SqlitePairedDeviceStore : IPairedDeviceStore, IDisposable
{
    private const int SchemaVersion = 2;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqlitePairedDeviceStore(string databasePath)
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
                CREATE TABLE IF NOT EXISTS device_schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS paired_devices (
                    device_id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    display_name_custom INTEGER NOT NULL DEFAULT 0,
                    manufacturer TEXT NULL,
                    model TEXT NULL,
                    identity_public_key TEXT NOT NULL,
                    certificate_fingerprint TEXT NOT NULL,
                    credential_key TEXT NOT NULL,
                    last_host TEXT NULL,
                    last_port INTEGER NULL,
                    last_instance_id TEXT NULL,
                    last_seen_at TEXT NULL,
                    last_connected_at TEXT NULL,
                    auto_connect INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'offline',
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS device_addresses (
                    device_id TEXT NOT NULL,
                    host TEXT NOT NULL,
                    port INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    last_success_at TEXT NULL,
                    last_failure_at TEXT NULL,
                    PRIMARY KEY (device_id, host, port),
                    FOREIGN KEY (device_id) REFERENCES paired_devices(device_id) ON DELETE CASCADE
                );
                """, cancellationToken).ConfigureAwait(false);

            await EnsureColumnAsync(
                connection,
                "paired_devices",
                "display_name_custom",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken).ConfigureAwait(false);

            await using var migration = connection.CreateCommand();
            migration.CommandText = """
                INSERT OR IGNORE INTO device_schema_migrations(version, applied_at)
                VALUES($version, $appliedAt);
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

    public async Task<IReadOnlyList<PairedDevice>> ListPairedDevicesAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, display_name, display_name_custom, manufacturer, model, identity_public_key,
                   certificate_fingerprint, credential_key, last_host, last_port,
                   last_instance_id, last_seen_at, last_connected_at, auto_connect,
                   status, created_at
            FROM paired_devices
            ORDER BY COALESCE(last_connected_at, created_at) DESC, display_name ASC;
            """;
        var devices = new List<PairedDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add(ReadDevice(reader));
        }

        return devices;
    }

    public async Task UpsertPairedDeviceAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO paired_devices(
                device_id, display_name, display_name_custom, manufacturer, model, identity_public_key,
                certificate_fingerprint, credential_key, last_host, last_port,
                last_instance_id, last_seen_at, last_connected_at, auto_connect,
                status, created_at)
            VALUES(
                $deviceId, $displayName, $displayNameCustom, $manufacturer, $model, $identityPublicKey,
                $certificateFingerprint, $credentialKey, $lastHost, $lastPort,
                $lastInstanceId, $lastSeenAt, $lastConnectedAt, $autoConnect,
                $status, $createdAt)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = excluded.display_name,
                display_name_custom = excluded.display_name_custom,
                manufacturer = excluded.manufacturer,
                model = excluded.model,
                identity_public_key = excluded.identity_public_key,
                certificate_fingerprint = excluded.certificate_fingerprint,
                credential_key = excluded.credential_key,
                last_host = excluded.last_host,
                last_port = excluded.last_port,
                last_instance_id = excluded.last_instance_id,
                last_seen_at = excluded.last_seen_at,
                last_connected_at = excluded.last_connected_at,
                auto_connect = excluded.auto_connect,
                status = excluded.status;
            """;
        AddDeviceParameters(command, device);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAddressAsync(DeviceAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_addresses(
                device_id, host, port, source, last_success_at, last_failure_at)
            VALUES($deviceId, $host, $port, $source, $lastSuccessAt, $lastFailureAt)
            ON CONFLICT(device_id, host, port) DO UPDATE SET
                source = excluded.source,
                last_success_at = excluded.last_success_at,
                last_failure_at = excluded.last_failure_at;
            """;
        command.Parameters.AddWithValue("$deviceId", address.DeviceId);
        command.Parameters.AddWithValue("$host", address.Host);
        command.Parameters.AddWithValue("$port", address.Port);
        command.Parameters.AddWithValue("$source", Format(address.Source));
        command.Parameters.AddWithValue("$lastSuccessAt", Db(address.LastSuccessAt));
        command.Parameters.AddWithValue("$lastFailureAt", Db(address.LastFailureAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePairedDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM paired_devices WHERE device_id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProbeSuccessAsync(
        PairedDevice device,
        string host,
        int port,
        string? instanceId,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        device.LastHost = host;
        device.LastPort = port;
        device.LastInstanceId = instanceId;
        device.LastSeenAt = seenAt;
        device.LastConnectedAt = seenAt;
        device.Status = PairedDeviceStatus.Online;
        await UpsertPairedDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        await UpsertAddressAsync(
            new DeviceAddress
            {
                DeviceId = device.DeviceId,
                Host = host,
                Port = port,
                Source = DeviceAddressSource.Saved,
                LastSuccessAt = seenAt,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProbeFailureAsync(
        string deviceId,
        string host,
        int port,
        PairedDeviceStatus status,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var deviceCommand = connection.CreateCommand())
        {
            deviceCommand.Transaction = (SqliteTransaction)transaction;
            deviceCommand.CommandText = """
                UPDATE paired_devices
                SET status = $status
                WHERE device_id = $deviceId;
                """;
            deviceCommand.Parameters.AddWithValue("$deviceId", deviceId);
            deviceCommand.Parameters.AddWithValue("$status", Format(status));
            await deviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var addressCommand = connection.CreateCommand())
        {
            addressCommand.Transaction = (SqliteTransaction)transaction;
            addressCommand.CommandText = """
                INSERT INTO device_addresses(device_id, host, port, source, last_failure_at)
                VALUES($deviceId, $host, $port, $source, $lastFailureAt)
                ON CONFLICT(device_id, host, port) DO UPDATE SET
                    last_failure_at = excluded.last_failure_at;
                """;
            addressCommand.Parameters.AddWithValue("$deviceId", deviceId);
            addressCommand.Parameters.AddWithValue("$host", host);
            addressCommand.Parameters.AddWithValue("$port", port);
            addressCommand.Parameters.AddWithValue("$source", Format(DeviceAddressSource.Saved));
            addressCommand.Parameters.AddWithValue("$lastFailureAt", Format(failedAt));
            await addressCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await ExecuteAsync(
            connection,
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};",
            cancellationToken).ConfigureAwait(false);
    }

    private static void AddDeviceParameters(SqliteCommand command, PairedDevice device)
    {
        command.Parameters.AddWithValue("$deviceId", device.DeviceId);
        command.Parameters.AddWithValue("$displayName", device.DisplayName);
        command.Parameters.AddWithValue("$displayNameCustom", device.IsDisplayNameCustom);
        command.Parameters.AddWithValue("$manufacturer", Db(device.Manufacturer));
        command.Parameters.AddWithValue("$model", Db(device.Model));
        command.Parameters.AddWithValue("$identityPublicKey", device.IdentityPublicKey);
        command.Parameters.AddWithValue("$certificateFingerprint", device.CertificateFingerprint);
        command.Parameters.AddWithValue("$credentialKey", device.CredentialKey);
        command.Parameters.AddWithValue("$lastHost", Db(device.LastHost));
        command.Parameters.AddWithValue("$lastPort", Db(device.LastPort));
        command.Parameters.AddWithValue("$lastInstanceId", Db(device.LastInstanceId));
        command.Parameters.AddWithValue("$lastSeenAt", Db(device.LastSeenAt));
        command.Parameters.AddWithValue("$lastConnectedAt", Db(device.LastConnectedAt));
        command.Parameters.AddWithValue("$autoConnect", device.AutoConnect);
        command.Parameters.AddWithValue("$status", Format(device.Status));
        command.Parameters.AddWithValue("$createdAt", Format(device.CreatedAt));
    }

    private static PairedDevice ReadDevice(SqliteDataReader reader) => new()
    {
        DeviceId = reader.GetString(0),
        DisplayName = reader.GetString(1),
        IsDisplayNameCustom = reader.GetBoolean(2),
        Manufacturer = NullableString(reader, 3),
        Model = NullableString(reader, 4),
        IdentityPublicKey = reader.GetString(5),
        CertificateFingerprint = reader.GetString(6),
        CredentialKey = reader.GetString(7),
        LastHost = NullableString(reader, 8),
        LastPort = NullableInt32(reader, 9),
        LastInstanceId = NullableString(reader, 10),
        LastSeenAt = NullableDateTimeOffset(reader, 11),
        LastConnectedAt = NullableDateTimeOffset(reader, 12),
        AutoConnect = reader.GetBoolean(13),
        Status = ParseStatus(reader.GetString(14)),
        CreatedAt = Parse(reader.GetString(15)),
    };

    private static PairedDeviceStatus ParseStatus(string value) =>
        Enum.Parse<PairedDeviceStatus>(value, ignoreCase: true);

    private static string Format(PairedDeviceStatus value) => value.ToString().ToLowerInvariant();
    private static string Format(DeviceAddressSource value) => value.ToString().ToLowerInvariant();
    private static object Db(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dateTime => Format(dateTime),
        _ => value,
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? NullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
}
