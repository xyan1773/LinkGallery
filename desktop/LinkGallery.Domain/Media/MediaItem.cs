namespace LinkGallery.Domain.Media;

public sealed class MediaItem
{
    public required string DeviceId { get; init; }
    public required string RemoteId { get; init; }
    public required string FileName { get; init; }
    public required MediaType Type { get; init; }
    public long FileSize { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMilliseconds { get; init; }
    public DateTimeOffset TakenAt { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public string? AlbumName { get; init; }
    public string? RelativePath { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? SourceDevice { get; init; }
    public string? SourceApplication { get; init; }
    public bool IsEditedExport { get; init; }
}

public enum MediaType
{
    Image,
    Video,
}

