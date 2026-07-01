using LinkGallery.Domain.Transfers;

namespace LinkGallery.Application.Transfers;

public interface ITransferJobStore
{
    Task<IReadOnlyList<TransferJob>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TransferJob job, CancellationToken cancellationToken = default);
}
