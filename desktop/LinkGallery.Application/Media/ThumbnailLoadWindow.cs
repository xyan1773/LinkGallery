namespace LinkGallery.Application.Media;

public readonly record struct ThumbnailLoadRange(int FirstIndex, int LastIndexExclusive);

public static class ThumbnailLoadWindow
{
    public static ThumbnailLoadRange Calculate(
        int itemCount,
        double viewportWidth,
        double viewportHeight,
        double verticalOffset,
        double itemWidth,
        double itemHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(viewportWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(viewportHeight, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(verticalOffset);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(itemWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(itemHeight, 0);

        var columns = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        var visibleRows = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemHeight));
        var firstVisibleRow = (int)Math.Floor(verticalOffset / itemHeight);
        var firstBufferedRow = Math.Max(0, firstVisibleRow - visibleRows);
        var lastBufferedRow = firstVisibleRow + (visibleRows * 2);
        return new ThumbnailLoadRange(
            Math.Min(itemCount, firstBufferedRow * columns),
            Math.Min(itemCount, (lastBufferedRow + 1) * columns));
    }
}
