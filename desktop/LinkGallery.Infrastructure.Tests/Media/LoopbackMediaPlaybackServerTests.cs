using System.Net;
using System.Net.Http.Headers;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class LoopbackMediaPlaybackServerTests
{
    [TestMethod]
    public async Task ServesFullAndRangeRequestsThroughAuthenticatedMediaSource()
    {
        byte[] content = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        var source = new RecordingMediaSource(content);
        using var server = new LoopbackMediaPlaybackServer(source, Video(content.Length));
        using var client = new HttpClient();

        var full = await client.GetByteArrayAsync(server.SourceUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.SourceUri);
        request.Headers.Range = new RangeHeaderValue(3, 6);
        using var response = await client.SendAsync(request);
        var partial = await response.Content.ReadAsByteArrayAsync();

        CollectionAssert.AreEqual(content, full);
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("bytes 3-6/10", response.Content.Headers.ContentRange?.ToString());
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6 }, partial);
        CollectionAssert.AreEqual(new long[] { 0, 3 }, source.Offsets);
    }

    [TestMethod]
    public async Task RejectsUnsatisfiableRangeWithoutOpeningRemoteContent()
    {
        var source = new RecordingMediaSource([0, 1, 2]);
        using var server = new LoopbackMediaPlaybackServer(source, Video(3));
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.SourceUri);
        request.Headers.Range = new RangeHeaderValue(10, null);

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.AreEqual("bytes */3", response.Content.Headers.ContentRange?.ToString());
        Assert.IsEmpty(source.Offsets);
    }

    private static MediaItem Video(long length) => new()
    {
        DeviceId = "phone-1",
        RemoteId = "video-1",
        FileName = "clip.mp4",
        Type = MediaType.Video,
        MimeType = "video/mp4",
        FileSize = length,
        TakenAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingMediaSource(byte[] content) : IReadOnlyMediaSource
    {
        public List<long> Offsets { get; } = [];

        public Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken)
        {
            Offsets.Add(offset);
            Stream stream = new MemoryStream(content[(int)offset..], writable: false);
            return Task.FromResult(stream);
        }
    }
}
