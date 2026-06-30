using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;

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

    private sealed class StubSource : IReadOnlyMediaSource
    {
        public bool Fail { get; init; }

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
                    },
                ],
                null));

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken) =>
            Fail
                ? Task.FromException<Stream>(new HttpRequestException("offline"))
                : Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
