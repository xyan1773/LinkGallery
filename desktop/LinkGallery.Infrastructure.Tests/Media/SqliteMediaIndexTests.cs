using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;
using Microsoft.Data.Sqlite;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class SqliteMediaIndexTests
{
    private static readonly string[] ExpectedOfflineMediaIds = ["media-1", "media-4"];
    private string _databasePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"linkgallery-{Guid.NewGuid():N}.db");
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
    public async Task MigrationCanRunRepeatedly()
    {
        var first = new SqliteMediaIndex(_databasePath);
        var second = new SqliteMediaIndex(_databasePath);

        await first.InitializeAsync();
        await first.InitializeAsync();
        await second.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 1),
                (SELECT COUNT(*) FROM sqlite_master
                 WHERE type = 'table' AND name IN ('devices', 'media_items', 'sync_cursors'));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(3L, reader.GetInt64(1));
    }

    [TestMethod]
    public async Task MoreThanFiveThousandItemsSyncWithoutDuplicatesAndNextRunIsIncremental()
    {
        var items = Enumerable.Range(1, 5_025)
            .Select(id => Item(id, modifiedSeconds: id))
            .ToArray();
        var source = new FakeMediaSource(items);
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index);

        var initial = await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        var cached = new List<MediaItem>();
        for (var offset = 0; ; offset += 500)
        {
            var batch = await index.SearchAsync(null, null, 500, offset, CancellationToken.None);
            cached.AddRange(batch);
            if (batch.Count < 500)
            {
                break;
            }
        }

        var incremental = await synchronizer.SynchronizeAsync(source, CancellationToken.None);

        Assert.IsTrue(initial.WasFullScan);
        Assert.AreEqual(26, initial.PagesFetched);
        Assert.AreEqual(5_025, initial.ItemsReceived);
        Assert.HasCount(5_025, cached);
        Assert.HasCount(5_025, cached.Select(item => item.RemoteId).Distinct().ToArray());
        Assert.IsFalse(incremental.WasFullScan);
        Assert.AreEqual(1, incremental.PagesFetched);
        Assert.AreEqual(0, incremental.ItemsReceived);
    }

    [TestMethod]
    public async Task NewAndRemovedItemsAreReconciledAndRemainSearchableOffline()
    {
        var source = new FakeMediaSource(
            Item(1, 1, "beach-one.jpg"),
            Item(2, 2, "beach-two.jpg"),
            Item(3, 3, "mountain.jpg"));
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 2);
        await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        source.Items =
        [
            Item(4, 4, "beach-new.jpg"),
            Item(3, 3, "mountain.jpg"),
            Item(1, 1, "beach-one.jpg"),
        ];

        var result = await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        var offlineResults = await index.SearchAsync(
            source.Device.Id,
            "beach",
            100,
            0,
            CancellationToken.None);

        Assert.IsTrue(result.WasFullScan);
        Assert.AreEqual(1, result.ItemsRemoved);
        CollectionAssert.AreEquivalent(
            ExpectedOfflineMediaIds,
            offlineResults.Select(item => item.RemoteId).ToArray());
    }

    private static MediaItem Item(int id, int modifiedSeconds, string? fileName = null) => new()
    {
        DeviceId = "phone-1",
        RemoteId = $"media-{id}",
        FileName = fileName ?? $"photo-{id:D5}.jpg",
        Type = MediaType.Image,
        FileSize = id * 10L,
        Width = 1920,
        Height = 1080,
        TakenAt = DateTimeOffset.UnixEpoch.AddSeconds(modifiedSeconds),
        ModifiedAt = DateTimeOffset.UnixEpoch.AddSeconds(modifiedSeconds),
        AlbumName = "Camera",
        RelativePath = "DCIM/Camera",
    };

    private sealed class FakeMediaSource(params MediaItem[] items) : IReadOnlyMediaSource
    {
        public MediaItem[] Items { get; set; } = items;

        public Device Device => new()
        {
            Id = "phone-1",
            Name = "Test phone",
            Platform = "android",
            MediaCount = Items.Length,
            Address = new Uri("http://phone/api/v1/"),
            IsOnline = true,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        public Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Device);

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken)
        {
            var ordered = Items
                .OrderByDescending(item => item.ModifiedAt)
                .ThenByDescending(item => item.RemoteId, StringComparer.Ordinal)
                .ToArray();
            var start = 0;
            if (query.Cursor is not null)
            {
                start = Array.FindIndex(ordered, item => item.RemoteId == query.Cursor) + 1;
            }

            var page = ordered.Skip(start).Take(query.Limit).ToArray();
            var next = start + page.Length < ordered.Length ? page[^1].RemoteId : null;
            return Task.FromResult(new MediaPage(page, next));
        }

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
