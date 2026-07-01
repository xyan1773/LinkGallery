using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Media;

public sealed class ThumbnailCacheReader : IMediaThumbnailCache
{
    private readonly string _directory;

    public ThumbnailCacheReader(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public bool IsThumbnailCached(MediaItem item, ThumbnailSize size)
    {
        ArgumentNullException.ThrowIfNull(item);
        return File.Exists(GetPath(item, size));
    }

    public Task<Stream?> OpenCachedThumbnailAsync(
        MediaItem item,
        ThumbnailSize size,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(item, size);
        Stream? stream = File.Exists(path)
            ? new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous)
            : null;
        return Task.FromResult(stream);
    }

    private string GetPath(MediaItem item, ThumbnailSize size) =>
        ThumbnailDiskCache.GetPath(
            _directory,
            new ThumbnailCacheKey(
                item.DeviceId,
                item.RemoteId,
                item.ModifiedAt.UtcTicks,
                size.Width,
                size.Height));
}
