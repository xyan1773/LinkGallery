namespace LinkGallery.Domain.Transfers;

public sealed record LocalCopy(
    string DeviceId,
    string RemoteId,
    string LocalPath,
    long FileSize,
    DateTimeOffset CopiedAt,
    string? Sha256 = null)
{
    public string DeviceId { get; init; } =
        !string.IsNullOrWhiteSpace(DeviceId)
            ? DeviceId
            : throw new ArgumentException("Device ID is required.", nameof(DeviceId));

    public string RemoteId { get; init; } =
        !string.IsNullOrWhiteSpace(RemoteId)
            ? RemoteId
            : throw new ArgumentException("Remote ID is required.", nameof(RemoteId));

    public string LocalPath { get; init; } =
        !string.IsNullOrWhiteSpace(LocalPath)
            ? LocalPath
            : throw new ArgumentException("Local path is required.", nameof(LocalPath));

    public long FileSize { get; init; } =
        FileSize >= 0
            ? FileSize
            : throw new ArgumentOutOfRangeException(nameof(FileSize));
}

