namespace LinkGallery.Application.Transfers;

public interface IRemoteTransferStatusSink
{
    Task PublishTransferStatusAsync(
        RemoteTransferStatus status,
        CancellationToken cancellationToken);
}

public sealed record RemoteTransferStatus(
    string TaskId,
    string DestinationName,
    int CompletedItems,
    int TotalItems,
    long CompletedBytes,
    long TotalBytes,
    string State,
    long Sequence,
    long ExpiresAtEpochMillis);
