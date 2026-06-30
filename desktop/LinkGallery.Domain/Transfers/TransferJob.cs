namespace LinkGallery.Domain.Transfers;

public sealed class TransferJob
{
    public TransferJob(
        Guid id,
        string deviceId,
        string remoteId,
        string destinationPath,
        long totalBytes)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Transfer ID cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);

        Id = id;
        DeviceId = deviceId;
        RemoteId = remoteId;
        DestinationPath = destinationPath;
        TotalBytes = totalBytes;
    }

    public Guid Id { get; }

    public string DeviceId { get; }

    public string RemoteId { get; }

    public string DestinationPath { get; }

    public long TotalBytes { get; }

    public long BytesTransferred { get; private set; }

    public TransferStatus Status { get; private set; } = TransferStatus.Pending;

    public string? FailureReason { get; private set; }

    public void Start()
    {
        EnsureStatus(TransferStatus.Pending, TransferStatus.Paused);
        Status = TransferStatus.Running;
        FailureReason = null;
    }

    public void ReportProgress(long bytesTransferred)
    {
        EnsureStatus(TransferStatus.Running);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesTransferred);

        if (bytesTransferred < BytesTransferred || bytesTransferred > TotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesTransferred),
                "Progress must be monotonic and cannot exceed the expected file size.");
        }

        BytesTransferred = bytesTransferred;
    }

    public void Pause()
    {
        EnsureStatus(TransferStatus.Running);
        Status = TransferStatus.Paused;
    }

    public void Complete()
    {
        EnsureStatus(TransferStatus.Running);
        if (BytesTransferred != TotalBytes)
        {
            throw new InvalidOperationException("A transfer cannot complete before all bytes arrive.");
        }

        Status = TransferStatus.Completed;
    }

    public void Fail(string reason)
    {
        EnsureStatus(TransferStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = TransferStatus.Failed;
        FailureReason = reason;
    }

    private void EnsureStatus(params TransferStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException(
                $"Transfer {Id} cannot change from {Status} in this operation.");
        }
    }
}

public enum TransferStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
}

