using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;

namespace LinkGallery.Application.Transfers;

public interface ITransferCoordinator : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task<TransferJob> EnqueueAsync(
        MediaItem media,
        string destinationDirectory,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default);

    Task PauseAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task ResumeAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default);

    IReadOnlyList<TransferJob> GetJobs();
}

public interface ITransferMediaSourceResolver
{
    ValueTask<IReadOnlyMediaSource> ResolveAsync(
        string deviceId,
        CancellationToken cancellationToken);
}
