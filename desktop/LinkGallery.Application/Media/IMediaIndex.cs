using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Application.Media;

public interface IMediaIndex
{
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
