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
    private static readonly string[] ExpectedIncrementalMediaIds = ["media-1", "media-3"];
    private static readonly string[] ExpectedRemainingMediaIds = ["media-1", "media-3"];
    private static readonly string[] ExpectedScreenshotAlbumIds =
        ["dcim/screenshots", "pictures/screenshots"];
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
                (SELECT COUNT(*) FROM schema_migrations WHERE version IN (1, 2)),
                (SELECT COUNT(*) FROM sqlite_master
                 WHERE type = 'table' AND name IN (
                    'devices', 'media_items', 'sync_cursors',
                    'device_sync_state', 'thumbnail_cache', 'albums'));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(2L, reader.GetInt64(0));
        Assert.AreEqual(6L, reader.GetInt64(1));
    }

    [TestMethod]
    public async Task VersionOneDatabaseMigratesWithoutLosingCachedMedia()
    {
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var setup = connection.CreateCommand();
            setup.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES(1, '2026-01-01T00:00:00Z');
                CREATE TABLE devices(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, platform TEXT NOT NULL, model TEXT NULL,
                    battery_percent INTEGER NULL, media_count INTEGER NOT NULL, address TEXT NULL,
                    is_online INTEGER NOT NULL, last_seen_at TEXT NOT NULL);
                INSERT INTO devices VALUES(
                    'phone-1', 'Phone', 'android', NULL, NULL, 1, NULL, 0, '2026-01-01T00:00:00Z');
                CREATE TABLE media_items(
                    device_id TEXT NOT NULL, remote_id TEXT NOT NULL, file_name TEXT NOT NULL,
                    media_type INTEGER NOT NULL, file_size INTEGER NOT NULL, width INTEGER NULL,
                    height INTEGER NULL, duration_ms INTEGER NULL, taken_at TEXT NOT NULL,
                    modified_at TEXT NOT NULL, album_name TEXT NULL, relative_path TEXT NULL,
                    source_device TEXT NULL, source_application TEXT NULL,
                    is_edited_export INTEGER NOT NULL, last_seen_at TEXT NOT NULL,
                    PRIMARY KEY(device_id, remote_id));
                INSERT INTO media_items VALUES(
                    'phone-1', 'legacy-1', 'legacy.jpg', 0, 42, 10, 10, NULL,
                    '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z',
                    'Screenshots', 'Pictures/Screenshots', NULL, NULL, 0,
                    '2026-01-01T00:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var index = new SqliteMediaIndex(_databasePath);
        await index.InitializeAsync();
        var media = await index.SearchAsync("phone-1", null, 10, 0, CancellationToken.None);
        var albums = await index.GetAlbumsAsync("phone-1", null, 10, 0, CancellationToken.None);

        Assert.HasCount(1, media);
        Assert.AreEqual("legacy-1", media[0].RemoteId);
        Assert.AreEqual("pictures/screenshots", media[0].AlbumId);
        Assert.HasCount(1, albums);
        Assert.AreEqual("pictures/screenshots", albums[0].AlbumId);

        await using var verify = new SqliteConnection($"Data Source={_databasePath}");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('media_items')
            WHERE name IN ('media_key', 'sort_time', 'generation', 'is_deleted', 'album_id');
            """;
        Assert.AreEqual(5L, (long)(await command.ExecuteScalarAsync())!);
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
    public async Task LargeLibrarySyncReportsReadablePageAndWriteProgress()
    {
        var items = Enumerable.Range(1, 1_421)
            .Select(id => Item(id, modifiedSeconds: id))
            .ToArray();
        var source = new FakeMediaSource(items);
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 200);
        var updates = new List<MediaSyncProgress>();

        var result = await synchronizer.SynchronizeAsync(
            source,
            new ProgressRecorder(updates),
            CancellationToken.None);

        Assert.IsTrue(result.WasFullScan);
        Assert.AreEqual(8, result.PagesFetched);
        Assert.AreEqual(1_421, result.ItemsReceived);
        Assert.IsTrue(updates.Any(update => update.Stage == MediaSyncStage.DeviceLoaded));
        Assert.IsTrue(updates.Any(update =>
            update.Stage == MediaSyncStage.FetchingPage &&
            update.PagesFetched == 0 &&
            update.TotalItems == 1_421));
        Assert.IsTrue(updates.Any(update =>
            update.Stage == MediaSyncStage.WritingPage &&
            update.PagesFetched == 8 &&
            update.ItemsReceived == 1_421));
        Assert.AreEqual(MediaSyncStage.Completed, updates[^1].Stage);
    }

    [TestMethod]
    public async Task SeededFullIndexContinuesFromInitialPageCursor()
    {
        var source = new FakeIncrementalMediaSource(
            Enumerable.Range(1, 450)
                .Select(id => Item(id, modifiedSeconds: id))
                .ToArray());
        var firstPage = await source.GetMediaPageAsync(
            new MediaQuery(Limit: 100),
            CancellationToken.None);
        source.RequestedCursors.Clear();
        var baseline = await source.GetSyncStateAsync(CancellationToken.None);
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 200);

        var result = await synchronizer.SynchronizeAsync(
            source,
            progress: null,
            new MediaSyncSeed(source.Device, firstPage, baseline),
            CancellationToken.None);

        Assert.IsTrue(result.WasFullScan);
        Assert.AreEqual(4, result.PagesFetched);
        Assert.AreEqual(450, result.ItemsReceived);
        Assert.HasCount(2, source.RequestedCursors);
        Assert.AreEqual(firstPage.NextCursor, source.RequestedCursors[0]);
        Assert.IsFalse(source.RequestedCursors.Any(static cursor => cursor is null));
    }

    [TestMethod]
    public async Task SearchFiltersByTypeAndDateRangeInsideSqlite()
    {
        var source = new FakeMediaSource(
            Item(1, (int)new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()),
            Item(2, (int)new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds(), type: MediaType.Image),
            Item(3, (int)new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds(), type: MediaType.Video));
        var index = new SqliteMediaIndex(_databasePath);
        await new IncrementalMediaIndexSynchronizer(index)
            .SynchronizeAsync(source, CancellationToken.None);

        var results = await index.SearchAsync(
            new MediaIndexQuery(
                source.Device.Id,
                SearchText: null,
                new HashSet<MediaType> { MediaType.Video },
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Limit: 100,
                Offset: 0),
            CancellationToken.None);

        Assert.HasCount(1, results);
        Assert.AreEqual("media-3", results[0].RemoteId);
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

    [TestMethod]
    public async Task OfflineAlbumsKeepSameNamesSeparatedByNormalizedPath()
    {
        var source = new FakeMediaSource(
            Item(1, 1, "one.jpg", "Pictures/Screenshots"),
            Item(2, 2, "two.jpg", "DCIM/Screenshots"),
            Item(3, 3, "three.jpg", "Pictures/Screenshots/"));
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 2);

        await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        var albums = await index.GetAlbumsAsync(
            source.Device.Id,
            "Screenshots",
            100,
            0,
            CancellationToken.None);
        var selectedAlbum = await index.SearchAsync(
            new MediaIndexQuery(
                DeviceId: source.Device.Id,
                SearchText: null,
                Types: null,
                FromInclusive: null,
                ToExclusive: null,
                Limit: 100,
                Offset: 0,
                AlbumId: "pictures/screenshots"),
            CancellationToken.None);

        Assert.HasCount(2, albums);
        CollectionAssert.AreEquivalent(
            ExpectedScreenshotAlbumIds,
            albums.Select(album => album.AlbumId).ToArray());
        Assert.AreEqual(3, albums.Sum(album => album.MediaCount));
        Assert.IsTrue(albums.All(album => album.CoverMediaId is not null));
        Assert.HasCount(2, selectedAlbum);
        Assert.IsTrue(selectedAlbum.All(item => item.AlbumId == "pictures/screenshots"));
    }

    [TestMethod]
    public async Task IncrementalPageAppliesUpsertsDeletesAndCursorInOneRun()
    {
        var source = new FakeIncrementalMediaSource(Item(1, 1), Item(2, 2));
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 2);
        var initial = await synchronizer.SynchronizeAsync(source, CancellationToken.None);

        source.Items = [Item(1, 10, "renamed.jpg"), Item(3, 3)];
        source.LatestCursor = "cursor-2";
        source.Changes =
        [
            new RemoteMediaChanges(
                source.LibraryVersion,
                "cursor-1",
                "cursor-2",
                "cursor-2",
                HasMore: false,
                Upserts: [source.Items[0], source.Items[1]],
                Deletes: []),
        ];
        var incremental = await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        var cached = await index.SearchAsync(source.Device.Id, null, 10, 0, CancellationToken.None);
        var unchanged = await synchronizer.SynchronizeAsync(source, CancellationToken.None);

        Assert.IsTrue(initial.WasFullScan);
        Assert.IsFalse(incremental.WasFullScan);
        Assert.AreEqual(2, incremental.PagesFetched);
        Assert.AreEqual(2, incremental.ItemsReceived);
        Assert.AreEqual(1, incremental.ItemsRemoved);
        Assert.HasCount(2, cached);
        CollectionAssert.AreEquivalent(
            ExpectedIncrementalMediaIds,
            cached.Select(item => item.RemoteId).ToArray());
        Assert.AreEqual("renamed.jpg", cached.Single(item => item.RemoteId == "media-1").FileName);
        Assert.IsFalse(unchanged.WasFullScan);
        Assert.AreEqual(1, unchanged.PagesFetched);
    }

    [TestMethod]
    public async Task ReducedRemoteCountForcesFullReconciliationForDeletion()
    {
        var source = new FakeIncrementalMediaSource(Item(1, 1), Item(2, 2), Item(3, 3));
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 2);
        await synchronizer.SynchronizeAsync(source, CancellationToken.None);

        source.Items = [Item(1, 1), Item(3, 3)];
        source.LatestCursor = "cursor-2";
        var result = await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        var cached = await index.SearchAsync(source.Device.Id, null, 10, 0, CancellationToken.None);

        Assert.IsTrue(result.WasFullScan);
        Assert.AreEqual(1, result.ItemsRemoved);
        CollectionAssert.AreEquivalent(
            ExpectedRemainingMediaIds,
            cached.Select(item => item.RemoteId).ToArray());
    }

    [TestMethod]
    public async Task LibraryVersionChangeForcesSafeFullIndex()
    {
        var source = new FakeIncrementalMediaSource(Item(1, 1), Item(2, 2));
        var index = new SqliteMediaIndex(_databasePath);
        var synchronizer = new IncrementalMediaIndexSynchronizer(index, pageSize: 2);
        await synchronizer.SynchronizeAsync(source, CancellationToken.None);

        source.LibraryVersion = "library-b";
        source.LatestCursor = "cursor-reset";
        source.Items = [Item(1, 10, "after-reset.jpg"), Item(2, 2)];
        var result = await synchronizer.SynchronizeAsync(source, CancellationToken.None);
        var cached = await index.SearchAsync(source.Device.Id, null, 10, 0, CancellationToken.None);

        Assert.IsTrue(result.WasFullScan);
        Assert.AreEqual("after-reset.jpg", cached.Single(item => item.RemoteId == "media-1").FileName);
    }

    private static MediaItem Item(
        int id,
        int modifiedSeconds,
        string? fileName = null,
        string relativePath = "DCIM/Camera",
        MediaType type = MediaType.Image) => new()
    {
        DeviceId = "phone-1",
        RemoteId = $"media-{id}",
        FileName = fileName ?? $"photo-{id:D5}.jpg",
        Type = type,
        FileSize = id * 10L,
        Width = 1920,
        Height = 1080,
        TakenAt = DateTimeOffset.UnixEpoch.AddSeconds(modifiedSeconds),
        ModifiedAt = DateTimeOffset.UnixEpoch.AddSeconds(modifiedSeconds),
        Generation = id,
        AlbumName = "Camera",
        RelativePath = relativePath,
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

    private sealed class FakeIncrementalMediaSource(params MediaItem[] items)
        : IReadOnlyMediaSource, IIncrementalMediaSource
    {
        public MediaItem[] Items { get; set; } = items;

        public string LibraryVersion { get; set; } = "library-a";

        public string LatestCursor { get; set; } = "cursor-1";

        public IReadOnlyList<RemoteMediaChanges> Changes { get; set; } = [];

        public List<string?> RequestedCursors { get; } = [];

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

        public Task<RemoteMediaSyncState> GetSyncStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteMediaSyncState(LibraryVersion, LatestCursor, Items.Length));

        public Task<RemoteMediaChanges> GetChangesAsync(
            string? after,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(Changes.Single(change => change.FromCursor == after));

        public Task<RemoteMediaManifestPage> GetManifestPageAsync(
            string? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            var ordered = Items.OrderBy(item => item.RemoteId, StringComparer.Ordinal).ToArray();
            var start = cursor is null
                ? 0
                : Array.FindIndex(ordered, item => item.RemoteId == cursor) + 1;
            var page = ordered.Skip(start).Take(limit).ToArray();
            var hasMore = start + page.Length < ordered.Length;
            return Task.FromResult(
                new RemoteMediaManifestPage(
                    LibraryVersion,
                    page.Select(item => new RemoteMediaManifestEntry(item.RemoteId, item.Generation)).ToArray(),
                    hasMore ? page[^1].RemoteId : null,
                    hasMore));
        }

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken)
        {
            RequestedCursors.Add(query.Cursor);
            var ordered = Items
                .OrderByDescending(item => item.ModifiedAt)
                .ThenByDescending(item => item.RemoteId, StringComparer.Ordinal)
                .ToArray();
            var start = query.Cursor is null
                ? 0
                : Array.FindIndex(ordered, item => item.RemoteId == query.Cursor) + 1;
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

    private sealed class ProgressRecorder(List<MediaSyncProgress> updates) : IProgress<MediaSyncProgress>
    {
        public void Report(MediaSyncProgress value) => updates.Add(value);
    }
}
