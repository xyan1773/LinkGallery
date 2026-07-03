using LinkGallery.Domain.Devices;
using LinkGallery.Infrastructure.Devices;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class SqlitePairedDeviceStoreTests
{
    private string _databasePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"linkgallery-devices-{Guid.NewGuid():N}.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task MigrationCreatesPairedDeviceAndAddressTables()
    {
        using var store = new SqlitePairedDeviceStore(_databasePath);

        await store.InitializeAsync();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM device_schema_migrations WHERE version = 1),
                (SELECT COUNT(*) FROM sqlite_master
                 WHERE type = 'table' AND name IN ('paired_devices', 'device_addresses'));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(2L, reader.GetInt64(1));
    }

    [TestMethod]
    public async Task ListsHistoricalPairedDevicesWithoutNetwork()
    {
        using var store = new SqlitePairedDeviceStore(_databasePath);
        var device = Device(status: PairedDeviceStatus.Offline);

        await store.UpsertPairedDeviceAsync(device, CancellationToken.None);

        var devices = await store.ListPairedDevicesAsync(CancellationToken.None);

        Assert.HasCount(1, devices);
        Assert.AreEqual("phone-1", devices[0].DeviceId);
        Assert.AreEqual(PairedDeviceStatus.Offline, devices[0].Status);
        Assert.AreEqual("192.168.1.20", devices[0].LastHost);
        Assert.AreEqual(39570, devices[0].LastPort);
    }

    [TestMethod]
    public async Task StoresOnlyCredentialKeyNotPlainAccessToken()
    {
        using var store = new SqlitePairedDeviceStore(_databasePath);
        var device = Device(status: PairedDeviceStatus.Offline);

        await store.UpsertPairedDeviceAsync(device, CancellationToken.None);

        var stored = (await store.ListPairedDevicesAsync(CancellationToken.None)).Single();
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('paired_devices');";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.AreEqual("credential-phone-1", stored.CredentialKey);
        CollectionAssert.Contains(columns, "credential_key");
        CollectionAssert.DoesNotContain(columns, "access_token");
    }

    [TestMethod]
    public async Task ProbeSuccessUpdatesDeviceAndSavedAddress()
    {
        using var store = new SqlitePairedDeviceStore(_databasePath);
        var seenAt = DateTimeOffset.Parse("2026-07-02T10:00:00Z", CultureInfo.InvariantCulture);
        var device = Device(status: PairedDeviceStatus.Checking);

        await store.UpsertPairedDeviceAsync(device, CancellationToken.None);
        await store.UpdateProbeSuccessAsync(device, "192.168.1.21", 39571, "instance-2", seenAt, CancellationToken.None);

        var updated = (await store.ListPairedDevicesAsync(CancellationToken.None)).Single();
        Assert.AreEqual(PairedDeviceStatus.Online, updated.Status);
        Assert.AreEqual("192.168.1.21", updated.LastHost);
        Assert.AreEqual(39571, updated.LastPort);
        Assert.AreEqual("instance-2", updated.LastInstanceId);
        Assert.AreEqual(seenAt, updated.LastConnectedAt);
    }

    [TestMethod]
    public async Task ProbeFailureDoesNotDeletePairedDevice()
    {
        using var store = new SqlitePairedDeviceStore(_databasePath);
        var device = Device(status: PairedDeviceStatus.Checking);

        await store.UpsertPairedDeviceAsync(device, CancellationToken.None);
        await store.UpdateProbeFailureAsync(
            device.DeviceId,
            device.LastHost!,
            device.LastPort!.Value,
            PairedDeviceStatus.Offline,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var updated = (await store.ListPairedDevicesAsync(CancellationToken.None)).Single();
        Assert.AreEqual(PairedDeviceStatus.Offline, updated.Status);
        Assert.AreEqual("phone-1", updated.DeviceId);
    }

    private static PairedDevice Device(PairedDeviceStatus status) => new()
    {
        DeviceId = "phone-1",
        DisplayName = "Pixel",
        Manufacturer = "Google",
        Model = "Pixel 9",
        IdentityPublicKey = "public-key",
        CertificateFingerprint = "AA:BB",
        CredentialKey = "credential-phone-1",
        LastHost = "192.168.1.20",
        LastPort = 39570,
        Status = status,
        CreatedAt = DateTimeOffset.Parse("2026-07-01T10:00:00Z", CultureInfo.InvariantCulture),
    };
}
