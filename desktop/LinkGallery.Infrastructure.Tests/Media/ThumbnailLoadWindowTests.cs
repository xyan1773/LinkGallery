using LinkGallery.Application.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class ThumbnailLoadWindowTests
{
    [TestMethod]
    public void FirstViewportLoadsOnlyVisibleRowsAndOneScreenBuffer()
    {
        var range = ThumbnailLoadWindow.Calculate(
            itemCount: 100,
            viewportWidth: 920,
            viewportHeight: 500,
            verticalOffset: 0,
            itemWidth: 184,
            itemHeight: 244);

        Assert.AreEqual(0, range.FirstIndex);
        Assert.AreEqual(35, range.LastIndexExclusive);
        Assert.IsLessThan(100, range.LastIndexExclusive);
    }

    [TestMethod]
    public void ScrollingMovesTheLoadWindowAndLeavesFarRowsOutside()
    {
        var range = ThumbnailLoadWindow.Calculate(
            itemCount: 100,
            viewportWidth: 920,
            viewportHeight: 500,
            verticalOffset: 2_440,
            itemWidth: 184,
            itemHeight: 244);

        Assert.AreEqual(35, range.FirstIndex);
        Assert.AreEqual(85, range.LastIndexExclusive);
        Assert.IsFalse(0 >= range.FirstIndex && 0 < range.LastIndexExclusive);
        Assert.IsTrue(50 >= range.FirstIndex && 50 < range.LastIndexExclusive);
    }
}
