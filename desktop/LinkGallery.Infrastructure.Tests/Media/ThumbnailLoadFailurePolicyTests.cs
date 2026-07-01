using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class ThumbnailLoadFailurePolicyTests
{
    [TestMethod]
    public void TimeoutConnectionAndProtocolFailuresKeepPlaceholder()
    {
        Assert.IsTrue(ThumbnailLoadFailurePolicy.KeepsPlaceholder(
            new MediaSourceTimeoutException(
                "thumbnail timed out",
                new OperationCanceledException())));
        Assert.IsTrue(ThumbnailLoadFailurePolicy.KeepsPlaceholder(
            new HttpRequestException("phone disconnected")));
        Assert.IsTrue(ThumbnailLoadFailurePolicy.KeepsPlaceholder(
            new MediaSourceProtocolException("invalid thumbnail")));
    }

    [TestMethod]
    public void UnexpectedFailuresAreNotSilentlyHidden()
    {
        Assert.IsFalse(ThumbnailLoadFailurePolicy.KeepsPlaceholder(
            new InvalidOperationException("programming error")));
    }
}
