using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;
using Microsoft.Data.Sqlite;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class CachingReadOnlyMediaSourceTests
{
    [TestMethod]
    public async Task CachedDevicePageAndThumbnailRemainAvailableOffline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var online = new StubSource();
            using var first = new CachingReadOnlyMediaSource(online, root, "phone-address", 1024);
            _ = await first.GetDeviceInfoAsync(CancellationToken.None);
            var onlinePage = await first.GetMediaPageAsync(
                new MediaQuery(Limit: 50),
                CancellationToken.None);
            await using (await first.OpenThumbnailAsync(
                "media-1",
                new ThumbnailSize(320, 240),
                CancellationToken.None))
            {
            }

            using var offline = new CachingReadOnlyMediaSource(
                new StubSource { Fail = true },
                root,
                "phone-address",
                1024);
            var device = await offline.GetDeviceInfoAsync(CancellationToken.None);
            var page = await offline.GetMediaPageAsync(
                new MediaQuery(Limit: 50),
                CancellationToken.None);
            await using var thumbnail = await offline.OpenThumbnailAsync(
                "media-1",
                new ThumbnailSize(320, 240),
                CancellationToken.None);

            Assert.IsFalse(device.IsOnline);
            Assert.IsTrue(offline.IsOffline);
            Assert.AreEqual(onlinePage.Items[0].RemoteId, page.Items[0].RemoteId);
            Assert.AreEqual(4, thumbnail.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task EntityAwareOriginalReadsAreForwardedForSafeResume()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inner = new StubSource();
            using var source = new CachingReadOnlyMediaSource(inner, root, "phone-address", 1024);

            await using var stream = await ((IEntityAwareMediaSource)source).OpenOriginalAsync(
                "media-1",
                4096,
                "\"sha256-test\"",
                CancellationToken.None);

            Assert.AreEqual(4096, inner.LastOffset);
            Assert.AreEqual("\"sha256-test\"", inner.LastEntityTag);
            Assert.AreEqual(4, stream.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task GenerationControlsThumbnailInvalidationAndSqliteMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "media-index.db");
        try
        {
            var inner = new StubSource { Generation = 42 };
            using var index = new SqliteMediaIndex(databasePath);
            using var source = new CachingReadOnlyMediaSource(
                inner,
                root,
                "phone-address",
                thumbnailCacheBytes: 1024,
                mediaIndex: index);
            _ = await source.GetDeviceInfoAsync(CancellationToken.None);
            _ = await source.GetMediaPageAsync(new MediaQuery(), CancellationToken.None);

            await using (await source.OpenThumbnailAsync(
                "media-1",
                new ThumbnailSize(256, 256),
                CancellationToken.None))
            {
            }
            await using (await source.OpenThumbnailAsync(
                "media-1",
                new ThumbnailSize(256, 256),
                CancellationToken.None))
            {
            }
            Assert.AreEqual(1, inner.ThumbnailFetchCount);

            inner.Generation = 43;
            _ = await source.GetMediaPageAsync(new MediaQuery(), CancellationToken.None);
            await using (await source.OpenThumbnailAsync(
                "media-1",
                new ThumbnailSize(256, 256),
                CancellationToken.None))
            {
            }
            Assert.AreEqual(2, inner.ThumbnailFetchCount);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MIN(generation), MAX(generation), SUM(file_size)
                FROM thumbnail_cache;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(2L, reader.GetInt64(0));
            Assert.AreEqual(42L, reader.GetInt64(1));
            Assert.AreEqual(43L, reader.GetInt64(2));
            Assert.AreEqual(8L, reader.GetInt64(3));

            await source.ClearThumbnailCacheAsync();
            reader.Close();
            command.CommandText = "SELECT COUNT(*) FROM thumbnail_cache;";
            Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class StubSource : IReadOnlyMediaSource, IEntityAwareMediaSource
    {
        public bool Fail { get; init; }

        public long LastOffset { get; private set; }

        public string? LastEntityTag { get; private set; }

        public long? Generation { get; set; }

        public int ThumbnailFetchCount { get; private set; }

        public Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
            Fail
                ? Task.FromException<Device>(new HttpRequestException("offline"))
                : Task.FromResult(new Device
                {
                    Id = "phone-1",
                    Name = "Phone",
                    Platform = "android",
                    MediaCount = 1,
                    IsOnline = true,
                    LastSeenAt = DateTimeOffset.UtcNow,
                });

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken) =>
            Fail
                ? Task.FromException<MediaPage>(new HttpRequestException("offline"))
                : Task.FromResult(new MediaPage(
                [
                    new MediaItem
                    {
                        DeviceId = "phone-1",
                        RemoteId = "media-1",
                        FileName = "photo.jpg",
                        Type = MediaType.Image,
                        ModifiedAt = DateTimeOffset.UnixEpoch,
                        TakenAt = DateTimeOffset.UnixEpoch,
                        Generation = Generation,
                    },
                ],
                null));

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return Task.FromException<Stream>(new HttpRequestException("offline"));
            }
            ThumbnailFetchCount++;
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));
        }

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            LastOffset = offset;
            LastEntityTag = entityTag;
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));
        }
    }
}
