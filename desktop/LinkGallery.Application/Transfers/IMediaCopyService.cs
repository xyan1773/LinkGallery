using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Application.Transfers;

public interface IMediaCopyService
{
    Task<CopyResult> CopyToLocalAsync(
        Device device,
        MediaItem media,
        string destinationDirectory,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record CopyProgress(long BytesTransferred, long TotalBytes)
{
    public double Fraction =>
        TotalBytes == 0
            ? 1
            : (double)BytesTransferred / TotalBytes;
}

public sealed record CopyResult(
    string LocalPath,
    long FileSize,
    string? Sha256);

