using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Application.Media;

public interface IReadOnlyMediaSource
{
    Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken);

    Task<MediaPage> GetMediaPageAsync(
        MediaQuery query,
        CancellationToken cancellationToken);

    Task<Stream> OpenThumbnailAsync(
        string remoteId,
        ThumbnailSize size,
        CancellationToken cancellationToken);

    Task<Stream> OpenOriginalAsync(
        string remoteId,
        long offset,
        CancellationToken cancellationToken);
}

public interface IMediaPlaybackUriSource
{
    Uri GetOriginalUri(string remoteId);
}

public interface IEntityAwareMediaSource
{
    Task<Stream> OpenOriginalAsync(
        string remoteId,
        long offset,
        string? entityTag,
        CancellationToken cancellationToken);
}

public interface IRemoteMediaStreamMetadata
{
    long? TotalLength { get; }

    DateTimeOffset? LastModified { get; }

    string? EntityTag { get; }
}

public interface IMediaThumbnailCache
{
    bool IsThumbnailCached(MediaItem item, ThumbnailSize size);

    Task<Stream?> OpenCachedThumbnailAsync(
        MediaItem item,
        ThumbnailSize size,
        CancellationToken cancellationToken);
}

public sealed record MediaQuery(
    string? Cursor = null,
    int Limit = 100,
    IReadOnlySet<MediaType>? Types = null);

public sealed record MediaPage(
    IReadOnlyList<MediaItem> Items,
    string? NextCursor);

public readonly record struct ThumbnailSize
{
    public ThumbnailSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 2048);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 2048);

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}
