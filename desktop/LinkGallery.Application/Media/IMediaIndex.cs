using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Application.Media;

public interface IMediaIndex
{
    Task<IReadOnlyList<MediaItem>> SearchAsync(
        MediaIndexQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaItem>> SearchAsync(
        string? deviceId,
        string? searchText,
        int limit,
        int offset,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaAlbum>> GetAlbumsAsync(
        string deviceId,
        string? searchText,
        int limit,
        int offset,
        CancellationToken cancellationToken);
}

public static class MediaIndexExtensions
{
    private const int MaximumPageSize = 500;

    public static async Task<IReadOnlyList<MediaItem>> SearchAllAsync(
        this IMediaIndex index,
        MediaIndexQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(query);

        var items = new List<MediaItem>();
        while (true)
        {
            var page = await index.SearchAsync(
                query with
                {
                    Limit = MaximumPageSize,
                    Offset = query.Offset + items.Count,
                },
                cancellationToken).ConfigureAwait(false);
            items.AddRange(page);
            if (page.Count < MaximumPageSize)
            {
                return items;
            }
        }
    }

    public static async Task<IReadOnlyList<MediaAlbum>> GetAllAlbumsAsync(
        this IMediaIndex index,
        string deviceId,
        string? searchText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var albums = new List<MediaAlbum>();
        while (true)
        {
            var page = await index.GetAlbumsAsync(
                deviceId,
                searchText,
                MaximumPageSize,
                albums.Count,
                cancellationToken).ConfigureAwait(false);
            albums.AddRange(page);
            if (page.Count < MaximumPageSize)
            {
                return albums;
            }
        }
    }
}

public sealed record MediaIndexQuery(
    string? DeviceId,
    string? SearchText,
    IReadOnlySet<MediaType>? Types,
    DateTimeOffset? FromInclusive,
    DateTimeOffset? ToExclusive,
    int Limit,
    int Offset,
    string? AlbumId = null,
    string? SourceDevice = null);

public sealed record MediaAlbum(
    string DeviceId,
    string AlbumId,
    string DisplayName,
    string? RelativePath,
    string? CoverMediaId,
    int MediaCount,
    int PhotoCount,
    int VideoCount,
    DateTimeOffset LatestSortTime);

public interface IMediaIndexSynchronizer
{
    Task<MediaSyncResult> SynchronizeAsync(
        IReadOnlyMediaSource source,
        CancellationToken cancellationToken);
}

public enum MediaSyncStage
{
    Connecting,
    DeviceLoaded,
    Paused,
    FetchingPage,
    WritingPage,
    Completing,
    Completed,
}

public sealed record MediaSyncProgress(
    MediaSyncStage Stage,
    Device? Device,
    int PagesFetched,
    int ItemsReceived,
    int? TotalItems,
    int ItemsRemoved,
    bool WasFullScan);

public sealed record MediaSyncResult(
    Device Device,
    int PagesFetched,
    int ItemsReceived,
    int ItemsRemoved,
    bool WasFullScan);

public sealed record MediaSyncSeed(
    Device Device,
    MediaPage FirstPage,
    RemoteMediaSyncState? BaselineState = null);
