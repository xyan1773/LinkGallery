using System.Net;
using System.Net.Sockets;
using System.Text;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class HttpReadOnlyMediaSourceTests
{
    [TestMethod]
    public void NormalizeApiAddressAddsSchemePortAndApiPath()
    {
        var result = HttpReadOnlyMediaSource.NormalizeApiAddress("192.168.1.8");

        Assert.AreEqual("http://192.168.1.8:39570/api/v1/", result.AbsoluteUri);
    }

    [TestMethod]
    public void EmulatorNatAddressIsAcceptedButIdentifiedForGuidance()
    {
        var result = HttpReadOnlyMediaSource.NormalizeApiAddress("10.0.2.15:39570");

        Assert.AreEqual("http://10.0.2.15:39570/api/v1/", result.AbsoluteUri);
        Assert.IsTrue(HttpReadOnlyMediaSource.IsPotentialAndroidEmulatorNatAddress(result));
    }

    [TestMethod]
    public void RegularLanAddressIsNotIdentifiedAsEmulatorNat()
    {
        var result = HttpReadOnlyMediaSource.NormalizeApiAddress("192.168.1.42:39570");

        Assert.IsFalse(HttpReadOnlyMediaSource.IsPotentialAndroidEmulatorNatAddress(result));
    }

    [TestMethod]
    public void OriginalUriTargetsReadOnlyContentEndpoint()
    {
        using var client = new HttpClient(new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)));
        var source = new HttpReadOnlyMediaSource(
            client,
            new Uri("http://phone:39570/api/v1/"));

        var result = source.GetOriginalUri("camera/photo 1");

        Assert.AreEqual(
            "http://phone:39570/api/v1/media/camera%2Fphoto%201/content",
            result.AbsoluteUri);
    }

    [TestMethod]
    public async Task OriginalStreamUsesRangeForNonzeroOffset()
    {
        string? range = null;
        var handler = new StubHandler(request =>
        {
            range = request.Headers.Range?.ToString();
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
        });
        using var client = new HttpClient(handler);
        var source = new HttpReadOnlyMediaSource(
            client,
            new Uri("http://phone:39570/api/v1/"));

        await using var stream = await source.OpenOriginalAsync(
            "video-1",
            1_048_576,
            CancellationToken.None);

        Assert.AreEqual("bytes=1048576-", range);
        Assert.AreEqual(3, stream.Length);
    }

    [TestMethod]
    public async Task DeviceAndMediaResponsesAreMappedToDomainModels()
    {
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/device" => Json(
                    """
                    {"id":"phone-1","name":"Pixel","platform":"android","model":"Pixel 9","battery":72,"mediaCount":1}
                    """),
                "/api/v1/media" => Json(
                    """
                    {"items":[{"id":"m1","fileName":"IMG.jpg","type":"image","fileSize":2048,"width":100,"height":80,"durationMilliseconds":null,"takenAt":"2026-06-30T01:00:00Z","modifiedAt":"2026-06-30T01:01:00Z","albumName":"Camera","relativePath":"DCIM/Camera","sourceDevice":null,"sourceApplication":null,"isEditedExport":false}],"nextCursor":null}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var client = new HttpClient(handler);
        var source = new HttpReadOnlyMediaSource(client, new Uri("http://phone:39570/api/v1/"));

        var device = await source.GetDeviceInfoAsync(CancellationToken.None);
        var page = await source.GetMediaPageAsync(
            new MediaQuery(Limit: 50, Types: new HashSet<MediaType> { MediaType.Image }),
            CancellationToken.None);

        Assert.AreEqual("Pixel", device.Name);
        Assert.AreEqual(72, device.BatteryPercent);
        Assert.AreEqual(1, device.MediaCount);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual("phone-1", page.Items[0].DeviceId);
        Assert.AreEqual(MediaType.Image, page.Items[0].Type);
        StringAssert.Contains(handler.LastRequestUri?.Query, "limit=50");
        StringAssert.Contains(handler.LastRequestUri?.Query, "type=image");
    }

    [TestMethod]
    public async Task ForbiddenProblemIsExposedAsTypedHttpException()
    {
        var handler = new StubHandler(_ => Json(
            """{"code":"permission_denied","message":"Media permission required"}""",
            HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        var source = new HttpReadOnlyMediaSource(client, new Uri("http://phone/api/v1/"));

        var exception = await Assert.ThrowsAsync<MediaSourceHttpException>(
            () => source.GetDeviceInfoAsync(CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
        StringAssert.Contains(exception.Message, "Media permission required");
    }

    [TestMethod]
    public async Task RequestPastDeadlineThrowsTypedTimeout()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var source = new HttpReadOnlyMediaSource(
            client,
            new Uri("http://phone/api/v1/"),
            TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<MediaSourceTimeoutException>(
            () => source.GetDeviceInfoAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RefusedConnectionThrowsTypedConnectionFailure()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException(
            "refused",
            new SocketException((int)SocketError.ConnectionRefused)));
        using var client = new HttpClient(handler);
        var source = new HttpReadOnlyMediaSource(client, new Uri("http://phone/api/v1/"));

        var exception = await Assert.ThrowsAsync<MediaSourceConnectionException>(
            () => source.GetDeviceInfoAsync(CancellationToken.None));

        Assert.AreEqual(MediaSourceConnectionFailure.ConnectionRefused, exception.Failure);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return _response(request, cancellationToken);
        }
    }
}
