using System.Text;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class ThumbnailDiskCacheTests
{
    [TestMethod]
    public async Task CacheHitDoesNotFetchAgain()
    {
        using var directory = new TemporaryDirectory();
        using var cache = new ThumbnailDiskCache(directory.Path, 1024);
        var fetchCount = 0;
        var key = new ThumbnailCacheKey("phone", "photo", 10, 320, 240);

        await using var first = await cache.GetOrCreateAsync(
            key,
            _ =>
            {
                fetchCount++;
                return Task.FromResult<Stream>(new MemoryStream("jpeg"u8.ToArray()));
            },
            CancellationToken.None);
        await using var second = await cache.GetOrCreateAsync(
            key,
            _ =>
            {
                fetchCount++;
                return Task.FromResult<Stream>(new MemoryStream("other"u8.ToArray()));
            },
            CancellationToken.None);

        Assert.AreEqual(1, fetchCount);
        using var reader = new StreamReader(second, Encoding.UTF8);
        Assert.AreEqual("jpeg", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task LeastRecentlyUsedFilesAreRemovedAtCapacity()
    {
        using var directory = new TemporaryDirectory();
        using var cache = new ThumbnailDiskCache(directory.Path, 6);

        for (var index = 0; index < 3; index++)
        {
            await using var stream = await cache.GetOrCreateAsync(
                new ThumbnailCacheKey("phone", $"photo-{index}", index, 1, 1),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])),
                CancellationToken.None);
        }

        var bytes = Directory.EnumerateFiles(directory.Path, "*.jpg")
            .Sum(static path => new FileInfo(path).Length);
        Assert.IsLessThanOrEqualTo(6, bytes);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LinkGallery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
