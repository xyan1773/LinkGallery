using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class LocalCopyCatalogTests
{
    [TestMethod]
    public async Task RegisteredExistingCopyCanBeResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mediaPath = Path.Combine(root, "photo.jpg");
            await File.WriteAllBytesAsync(mediaPath, [1, 2, 3]);
            using var catalog = new LocalCopyCatalog(Path.Combine(root, "copies.json"));
            var item = Item(fileSize: 3);

            await catalog.RegisterAsync(new LocalCopy(
                item.DeviceId,
                item.RemoteId,
                mediaPath,
                item.FileSize,
                DateTimeOffset.UtcNow));
            var result = await catalog.FindAsync(item);

            Assert.IsNotNull(result);
            Assert.AreEqual(mediaPath, result.LocalPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task MissingOrOutdatedCopyIsNotReturned()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mediaPath = Path.Combine(root, "missing.jpg");
            using var catalog = new LocalCopyCatalog(Path.Combine(root, "copies.json"));
            var item = Item(fileSize: 3);
            await catalog.RegisterAsync(new LocalCopy(
                item.DeviceId,
                item.RemoteId,
                mediaPath,
                item.FileSize,
                DateTimeOffset.UtcNow));

            Assert.IsNull(await catalog.FindAsync(item));
            Assert.IsNull(await catalog.FindAsync(Item(fileSize: 4)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static MediaItem Item(long fileSize) => new()
    {
        DeviceId = "phone-1",
        RemoteId = "media-1",
        FileName = "photo.jpg",
        Type = MediaType.Image,
        FileSize = fileSize,
        TakenAt = DateTimeOffset.UnixEpoch,
        ModifiedAt = DateTimeOffset.UnixEpoch,
    };
}
