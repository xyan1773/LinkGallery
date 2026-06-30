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

public sealed record MediaQuery(
    string? Cursor = null,
    int Limit = 100,
    IReadOnlySet<MediaType>? Types = null);

public sealed record MediaPage(
    IReadOnlyList<MediaItem> Items,
    string? NextCursor);

public readonly record struct ThumbnailSize(int Width, int Height);

