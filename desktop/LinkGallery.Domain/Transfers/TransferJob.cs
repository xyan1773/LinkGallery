namespace LinkGallery.Domain.Transfers;

public sealed class TransferJob
{
    public TransferJob(
        Guid id,
        string deviceId,
        string remoteId,
        string destinationPath,
        long totalBytes,
        DateTimeOffset? remoteModifiedAt = null,
        string? expectedSha256 = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Transfer ID cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ValidateSha256(expectedSha256, nameof(expectedSha256));

        Id = id;
        DeviceId = deviceId;
        RemoteId = remoteId;
        DestinationPath = Path.GetFullPath(destinationPath);
        TotalBytes = totalBytes;
        RemoteModifiedAt = remoteModifiedAt;
        ExpectedSha256 = expectedSha256?.ToLowerInvariant();
    }

    public Guid Id { get; }

    public string DeviceId { get; }

    public string RemoteId { get; }

    public string DestinationPath { get; }

    public string PartialPath => $"{DestinationPath}.partial";

    public long TotalBytes { get; }

    public DateTimeOffset? RemoteModifiedAt { get; }

    public string? ExpectedSha256 { get; }

    public string? VerifiedSha256 { get; private set; }

    public string? RemoteEntityTag { get; private set; }

    public long BytesTransferred { get; private set; }

    public TransferStatus Status { get; private set; } = TransferStatus.Pending;

    public int AttemptCount { get; private set; }

    public DateTimeOffset? RetryAfter { get; private set; }

    public string? FailureReason { get; private set; }

    public bool IsTerminal => Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled;

    public void Start()
    {
        EnsureStatus(TransferStatus.Pending, TransferStatus.Retrying);
        Status = TransferStatus.Running;
        AttemptCount++;
        RetryAfter = null;
        FailureReason = null;
    }

    public void ReconcilePartialLength(long bytesTransferred)
    {
        EnsureStatus(TransferStatus.Pending, TransferStatus.Paused, TransferStatus.Retrying);
        ValidateProgress(bytesTransferred);
        BytesTransferred = bytesTransferred;
    }

    public void ReportProgress(long bytesTransferred)
    {
        EnsureStatus(TransferStatus.Running);
        ValidateProgress(bytesTransferred);
        if (bytesTransferred < BytesTransferred)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesTransferred),
                "Progress must be monotonic.");
        }

        BytesTransferred = bytesTransferred;
    }

    public void CaptureRemoteEntityTag(string entityTag)
    {
        EnsureStatus(TransferStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityTag);
        if (RemoteEntityTag is not null &&
            !string.Equals(RemoteEntityTag, entityTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote entity changed during transfer.");
        }

        RemoteEntityTag = entityTag;
    }

    public void Pause()
    {
        EnsureStatus(TransferStatus.Pending, TransferStatus.Running, TransferStatus.Retrying);
        Status = TransferStatus.Paused;
        RetryAfter = null;
    }

    public void Resume()
    {
        EnsureStatus(TransferStatus.Paused);
        Status = TransferStatus.Pending;
        FailureReason = null;
    }

    public void Cancel()
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException($"Transfer {Id} is already {Status}.");
        }

        Status = TransferStatus.Cancelled;
        RetryAfter = null;
        FailureReason = null;
    }

    public void ScheduleRetry(string reason, DateTimeOffset retryAfter)
    {
        EnsureStatus(TransferStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = TransferStatus.Retrying;
        FailureReason = reason;
        RetryAfter = retryAfter;
    }

    public void Complete(string? verifiedSha256 = null)
    {
        EnsureStatus(TransferStatus.Running);
        if (BytesTransferred != TotalBytes)
        {
            throw new InvalidOperationException("A transfer cannot complete before all bytes arrive.");
        }

        ValidateSha256(verifiedSha256, nameof(verifiedSha256));
        if (ExpectedSha256 is not null &&
            !string.Equals(ExpectedSha256, verifiedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The transferred file hash does not match the expected SHA-256.");
        }

        VerifiedSha256 = verifiedSha256?.ToLowerInvariant();
        Status = TransferStatus.Completed;
        FailureReason = null;
        RetryAfter = null;
    }

    public void Fail(string reason)
    {
        EnsureStatus(TransferStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = TransferStatus.Failed;
        FailureReason = reason;
        RetryAfter = null;
    }

    public void RecoverAfterRestart()
    {
        if (Status == TransferStatus.Running)
        {
            Status = TransferStatus.Pending;
            FailureReason = "Interrupted while the application was not running.";
        }
    }

    public static TransferJob Restore(TransferJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var job = new TransferJob(
            snapshot.Id,
            snapshot.DeviceId,
            snapshot.RemoteId,
            snapshot.DestinationPath,
            snapshot.TotalBytes,
            snapshot.RemoteModifiedAt,
            snapshot.ExpectedSha256);
        job.ValidateProgress(snapshot.BytesTransferred);
        ValidateSha256(snapshot.VerifiedSha256, nameof(snapshot));
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.AttemptCount);

        if (snapshot.Status == TransferStatus.Completed && snapshot.BytesTransferred != snapshot.TotalBytes)
        {
            throw new ArgumentException("A persisted completed transfer has an invalid byte count.", nameof(snapshot));
        }

        if (snapshot.Status == TransferStatus.Retrying && snapshot.RetryAfter is null)
        {
            throw new ArgumentException("A retrying transfer must have a retry time.", nameof(snapshot));
        }

        job.BytesTransferred = snapshot.BytesTransferred;
        job.Status = snapshot.Status;
        job.AttemptCount = snapshot.AttemptCount;
        job.RetryAfter = snapshot.RetryAfter;
        job.FailureReason = snapshot.FailureReason;
        job.VerifiedSha256 = snapshot.VerifiedSha256?.ToLowerInvariant();
        job.RemoteEntityTag = snapshot.RemoteEntityTag;
        return job;
    }

    public TransferJobSnapshot ToSnapshot() =>
        new(
            Id,
            DeviceId,
            RemoteId,
            DestinationPath,
            TotalBytes,
            BytesTransferred,
            Status,
            AttemptCount,
            RetryAfter,
            FailureReason,
            RemoteModifiedAt,
            ExpectedSha256,
            VerifiedSha256,
            RemoteEntityTag);

    private void ValidateProgress(long bytesTransferred)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesTransferred);
        if (bytesTransferred > TotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesTransferred),
                "Progress cannot exceed the expected file size.");
        }
    }

    private static void ValidateSha256(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", parameterName);
        }
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

public sealed record TransferJobSnapshot(
    Guid Id,
    string DeviceId,
    string RemoteId,
    string DestinationPath,
    long TotalBytes,
    long BytesTransferred,
    TransferStatus Status,
    int AttemptCount,
    DateTimeOffset? RetryAfter,
    string? FailureReason,
    DateTimeOffset? RemoteModifiedAt,
    string? ExpectedSha256,
    string? VerifiedSha256,
    string? RemoteEntityTag);

public enum TransferStatus
{
    Pending,
    Running,
    Paused,
    Retrying,
    Completed,
    Failed,
    Cancelled,
}
