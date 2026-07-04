using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class ThumbnailCacheReaderTests
{
    [TestMethod]
    public async Task CachedThumbnailRemainsDiscoverableWithoutConnectedSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var item = new MediaItem
            {
                DeviceId = "phone-1",
                RemoteId = "photo-1",
                FileName = "photo.jpg",
                Type = MediaType.Image,
                TakenAt = DateTimeOffset.UnixEpoch,
                ModifiedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
                Generation = 7,
            };
            var size = new ThumbnailSize(320, 240);
            using (var cache = new ThumbnailDiskCache(root, 1024))
            {
                await using var created = await cache.GetOrCreateAsync(
                    ThumbnailCacheKey.Create(item, size),
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
                    CancellationToken.None);
            }

            var reader = new ThumbnailCacheReader(root);
            await using var cached = await reader.OpenCachedThumbnailAsync(
                item,
                size,
                CancellationToken.None);

            Assert.IsTrue(reader.IsThumbnailCached(item, size));
            Assert.IsNotNull(cached);
            Assert.AreEqual(3, cached.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
