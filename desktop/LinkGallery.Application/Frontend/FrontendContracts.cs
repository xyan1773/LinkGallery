using LinkGallery.Domain.Media;

namespace LinkGallery.Application.Frontend;

public enum FrontendRoute
{
    Photos,
    Albums,
    Album,
    Devices,
    Settings,
}

public enum FrontendAlbumKind
{
    Smart,
    Device,
    Custom,
}

public enum FrontendMediaFilter
{
    All,
    Photos,
    Videos,
    Favorites,
}

public sealed record FrontendAlbum(
    string Id,
    string Name,
    FrontendAlbumKind Kind,
    int ItemCount,
    int PhotoCount,
    int VideoCount,
    string? Source,
    string? CoverMediaId);

public sealed record FrontendMediaQuery(
    string DeviceId,
    FrontendMediaFilter Filter,
    string? AlbumId,
    string? Search,
    int Limit,
    int Offset);

public interface IFrontendMediaService
{
    Task<IReadOnlyList<MediaItem>> ListMediaAsync(
        FrontendMediaQuery query,
        CancellationToken cancellationToken);
}

public interface IFrontendAlbumService
{
    Task<IReadOnlyList<FrontendAlbum>> ListAlbumsAsync(
        string deviceId,
        CancellationToken cancellationToken);
}

public interface IFavoriteMediaStore
{
    Task<bool> IsFavoriteAsync(
        string deviceId,
        string mediaId,
        CancellationToken cancellationToken);

    Task SetFavoriteAsync(
        string deviceId,
        string mediaId,
        bool isFavorite,
        CancellationToken cancellationToken);
}

public interface IUserAlbumStore
{
    Task<IReadOnlyList<FrontendAlbum>> ListUserAlbumsAsync(
        string deviceId,
        CancellationToken cancellationToken);
}
