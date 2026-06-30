namespace LinkGallery.Infrastructure.Media;

public static class ThumbnailLoadFailurePolicy
{
    public static bool KeepsPlaceholder(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is MediaSourceTimeoutException or
            HttpRequestException or
            MediaSourceProtocolException;
    }
}
