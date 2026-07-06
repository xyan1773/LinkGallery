using System.Net;
using System.Text;
using LinkGallery.Application.Devices;
using LinkGallery.Infrastructure.Devices;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class HttpPairingClientTests
{
    [TestMethod]
    public async Task StartsAndConfirmsPairingUsingCurrentProtocol()
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(
            new StubHandler(request =>
            {
                requests.Add(Clone(request));
                var body = request.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)
                    ? """
                      {"pairingSessionId":"session-1","phoneNonce":"nonce","expiresAtEpochMillis":2000000000000,"attemptsRemaining":5,"codeLength":6}
                      """
                    : """
                      {"paired":true,"accessToken":"secret-token","tokenType":"Bearer"}
                      """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }));
        var client = new HttpPairingClient(httpClient);
        var identity = new PairingIdentity("desktop-1", "Desktop", "Windows", "identity", "ephemeral", "nonce");

        var session = await client.StartAsync(
            new Uri("http://phone:39570/api/v1/"),
            identity,
            CancellationToken.None);
        var credential = await client.ConfirmAsync(
            new Uri("http://phone:39570/api/v1/"),
            session.PairingSessionId,
            "123456",
            CancellationToken.None);

        Assert.AreEqual("session-1", session.PairingSessionId);
        Assert.AreEqual("secret-token", credential.AccessToken);
        Assert.AreEqual("/api/v1/pair/start", requests[0].RequestUri!.AbsolutePath);
        StringAssert.Contains(await requests[0].Content!.ReadAsStringAsync(), "\"desktopId\":\"desktop-1\"");
        StringAssert.Contains(await requests[1].Content!.ReadAsStringAsync(), "\"verificationCode\":\"123456\"");
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            clone.Content = new StringContent(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        return clone;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
