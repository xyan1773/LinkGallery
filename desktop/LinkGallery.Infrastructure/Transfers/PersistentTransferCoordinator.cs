using System.Net;
using System.Security.Cryptography;
using LinkGallery.Application.Media;
using LinkGallery.Application.Transfers;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Infrastructure.Transfers;

public sealed class PersistentTransferCoordinator : ITransferCoordinator
{
    private const int BufferSize = 128 * 1024;
    private const long PersistenceInterval = 1024 * 1024;

    private readonly ITransferJobStore _store;
    private readonly ITransferMediaSourceResolver _sourceResolver;
    private readonly TransferCoordinatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITransferFileSystem _fileSystem;
    private readonly LocalCopyCatalog? _localCopies;
    private readonly Dictionary<Guid, TransferJob> _jobs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _activeTransfers = [];
    private readonly HashSet<Guid> _committingTransfers = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private Task[]? _workers;
    private bool _disposed;

    public PersistentTransferCoordinator(
        ITransferJobStore store,
        ITransferMediaSourceResolver sourceResolver,
        TransferCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null,
        ITransferFileSystem? fileSystem = null,
        LocalCopyCatalog? localCopies = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sourceResolver);
        _store = store;
        _sourceResolver = sourceResolver;
        _options = options ?? new TransferCoordinatorOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fileSystem = fileSystem ?? new PhysicalTransferFileSystem();
        _localCopies = localCopies;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_workers is not null)
            {
                throw new InvalidOperationException("The transfer queue has already started.");
            }
        }

        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in loaded)
        {
            job.RecoverAfterRestart();
            if (job.Status == TransferStatus.Completed &&
                !_fileSystem.FileExists(job.DestinationPath))
            {
                job.MarkCompletedFileMissing();
            }

            lock (_sync)
            {
                if (!_jobs.TryAdd(job.Id, job))
                {
                    throw new InvalidDataException($"The transfer queue contains duplicate job ID {job.Id}.");
                }
            }

            await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _workers = Enumerable.Range(0, _options.MaxConcurrentTransfers)
                .Select(_ => Task.Run(() => WorkerAsync(_lifetime.Token), CancellationToken.None))
                .ToArray();
        }

        SignalWorkers();
    }

    public async Task<TransferJob> EnqueueAsync(
        MediaItem media,
        string destinationDirectory,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (_localCopies is not null)
        {
            var existingCopy = await _localCopies.FindAsync(media, cancellationToken)
                .ConfigureAwait(false);
            if (existingCopy is not null)
            {
                return await AddCompletedDuplicateAsync(media, existingCopy, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        lock (_sync)
        {
            var existingJob = _jobs.Values.FirstOrDefault(job =>
                job.Status != TransferStatus.Cancelled &&
                job.DeviceId == media.DeviceId &&
                job.RemoteId == media.RemoteId &&
                job.TotalBytes == media.FileSize &&
                SameInstant(job.RemoteModifiedAt, media.ModifiedAt));
            if (existingJob is not null)
            {
                return TransferJob.Restore(existingJob.ToSnapshot());
            }
        }

        var fileName = Path.GetFileName(media.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("The media file name is invalid.", nameof(media));
        }

        var destinationPath = GetAvailableDestinationPath(destinationDirectory, fileName);
        var job = new TransferJob(
            Guid.NewGuid(),
            media.DeviceId,
            media.RemoteId,
            destinationPath,
            media.FileSize,
            media.ModifiedAt,
            expectedSha256);
        lock (_sync)
        {
            var existingJob = _jobs.Values.FirstOrDefault(candidate =>
                candidate.Status != TransferStatus.Cancelled &&
                candidate.DeviceId == media.DeviceId &&
                candidate.RemoteId == media.RemoteId &&
                candidate.TotalBytes == media.FileSize &&
                SameInstant(candidate.RemoteModifiedAt, media.ModifiedAt));
            if (existingJob is not null)
            {
                return TransferJob.Restore(existingJob.ToSnapshot());
            }

            _jobs.Add(job.Id, job);
        }

        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        SignalWorkers();
        return TransferJob.Restore(job.ToSnapshot());
    }

    public async Task PauseAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        TransferJob job;
        lock (_sync)
        {
            job = GetJob(jobId);
            if (_committingTransfers.Contains(jobId) ||
                job.Status is TransferStatus.Paused or TransferStatus.Completed or
                TransferStatus.Failed or TransferStatus.Cancelled)
            {
                return;
            }

            job.Pause();
            CancelActiveTransfer(jobId);
        }

        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        TransferJob job;
        lock (_sync)
        {
            job = GetJob(jobId);
            if (job.Status != TransferStatus.Paused)
            {
                return;
            }

            job.Resume();
        }

        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        SignalWorkers();
    }

    public async Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        TransferJob job;
        lock (_sync)
        {
            job = GetJob(jobId);
            if (job.Status != TransferStatus.Failed)
            {
                return;
            }

            job.Retry();
        }

        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        SignalWorkers();
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        TransferJob job;
        lock (_sync)
        {
            job = GetJob(jobId);
            if (_committingTransfers.Contains(jobId))
            {
                return;
            }

            job.Cancel();
            CancelActiveTransfer(jobId);
        }

        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAllAsync(CancellationToken cancellationToken = default)
    {
        Guid[] jobIds;
        lock (_sync)
        {
            jobIds = _jobs.Values
                .Where(job => job.Status is TransferStatus.Pending or
                    TransferStatus.Running or TransferStatus.Retrying)
                .Select(job => job.Id)
                .ToArray();
        }

        foreach (var jobId in jobIds)
        {
            await PauseAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        Guid[] jobIds;
        lock (_sync)
        {
            jobIds = _jobs.Values
                .Where(job => job.Status == TransferStatus.Paused)
                .Select(job => job.Id)
                .ToArray();
        }

        foreach (var jobId in jobIds)
        {
            await ResumeAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearCompletedAsync(CancellationToken cancellationToken = default)
    {
        Guid[] jobIds;
        lock (_sync)
        {
            jobIds = _jobs.Values
                .Where(job => job.Status == TransferStatus.Completed)
                .Select(job => job.Id)
                .ToArray();
        }

        foreach (var jobId in jobIds)
        {
            await _store.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _jobs.Remove(jobId);
            }
        }
    }

    public IReadOnlyList<TransferJob> GetJobs()
    {
        lock (_sync)
        {
            return _jobs.Values
                .OrderBy(job => job.Id)
                .Select(job => TransferJob.Restore(job.ToSnapshot()))
                .ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        Task[] workers;
        lock (_sync)
        {
            foreach (var cancellation in _activeTransfers.Values)
            {
                cancellation.Cancel();
            }

            workers = _workers ?? [];
        }

        SignalWorkers();
        await Task.WhenAll(workers).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var cancellation in _activeTransfers.Values)
            {
                cancellation.Dispose();
            }

            _activeTransfers.Clear();
        }

        _signal.Dispose();
        _lifetime.Dispose();
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TransferJob? job = null;
            CancellationTokenSource? transferCancellation = null;
            try
            {
                lock (_sync)
                {
                    job = FindRunnableJob();
                    if (job is not null)
                    {
                        PrepareAndStart(job);
                        transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _activeTransfers.Add(job.Id, transferCancellation);
                    }
                }

                if (job is null || transferCancellation is null)
                {
                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                await TransferAsync(job, transferCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested ||
                transferCancellation?.IsCancellationRequested == true)
            {
                // Pause, cancel, and shutdown deliberately interrupt active I/O.
            }
            catch (Exception exception) when (job is not null)
            {
                await HandleFailureAsync(job, exception, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (job is not null)
                {
                    lock (_sync)
                    {
                        _activeTransfers.Remove(job.Id);
                    }
                }

                transferCancellation?.Dispose();
            }
        }
    }

    private TransferJob? FindRunnableJob()
    {
        var now = _timeProvider.GetUtcNow();
        return _jobs.Values.FirstOrDefault(job =>
            job.Status == TransferStatus.Pending ||
            (job.Status == TransferStatus.Retrying && job.RetryAfter <= now));
    }

    private void PrepareAndStart(TransferJob job)
    {
        var partialLength = _fileSystem.FileExists(job.PartialPath)
            ? _fileSystem.GetFileLength(job.PartialPath)
            : 0;
        if (partialLength > job.TotalBytes)
        {
            job.Start();
            throw new TransferPermanentException(
                "The partial file is larger than the remote file; the remote file may have changed.");
        }

        job.ReconcilePartialLength(partialLength);
        job.Start();
    }

    private async Task TransferAsync(TransferJob job, CancellationToken cancellationToken)
    {
        _fileSystem.CreateDirectory(Path.GetDirectoryName(job.DestinationPath)!);
        if (_fileSystem.FileExists(job.DestinationPath))
        {
            await CompleteExistingDestinationAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (job.BytesTransferred < job.TotalBytes)
        {
            var source = await _sourceResolver.ResolveAsync(job.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            if (job.BytesTransferred > 0 && job.RemoteEntityTag is null)
            {
                throw new TransferPermanentException(
                    "The partial file has no entity validator and cannot be resumed safely.");
            }

            await using var remote = source is IEntityAwareMediaSource entityAwareSource
                ? await entityAwareSource.OpenOriginalAsync(
                    job.RemoteId,
                    job.BytesTransferred,
                    job.RemoteEntityTag,
                    cancellationToken).ConfigureAwait(false)
                : job.BytesTransferred == 0
                    ? await source.OpenOriginalAsync(
                        job.RemoteId,
                        job.BytesTransferred,
                        cancellationToken).ConfigureAwait(false)
                    : throw new TransferPermanentException(
                        "The media source does not support entity-safe resume.");
            if (remote is IRemoteMediaStreamMetadata metadata)
            {
                if (metadata.TotalLength is long remoteLength && remoteLength != job.TotalBytes)
                {
                    throw new TransferPermanentException(
                        "The remote file size changed after this transfer was queued.");
                }

                if (metadata.LastModified is DateTimeOffset lastModified &&
                    job.RemoteModifiedAt is DateTimeOffset expectedModifiedAt &&
                    Math.Abs((lastModified - expectedModifiedAt).TotalSeconds) >= 1)
                {
                    throw new TransferPermanentException(
                        "The remote file changed after this transfer was queued.");
                }

                if (string.IsNullOrWhiteSpace(metadata.EntityTag))
                {
                    throw new TransferPermanentException(
                        "The phone did not provide an entity validator for this file.");
                }

                if (job.RemoteEntityTag is not null &&
                    !string.Equals(job.RemoteEntityTag, metadata.EntityTag, StringComparison.Ordinal))
                {
                    throw new TransferPermanentException(
                        "The remote file changed after this transfer started.");
                }

                lock (_sync)
                {
                    job.CaptureRemoteEntityTag(metadata.EntityTag);
                }

                await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new TransferPermanentException(
                    "The phone did not provide resumable entity metadata.");
            }

            await using var local = _fileSystem.OpenPartialWrite(
                job.PartialPath,
                job.BytesTransferred);

            var buffer = new byte[BufferSize];
            var lastPersisted = job.BytesTransferred;
            while (true)
            {
                var read = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (local.Position + read > job.TotalBytes)
                {
                    throw new TransferPermanentException(
                        "The remote file grew during transfer; its metadata is no longer current.");
                }

                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (job.Status != TransferStatus.Running)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(cancellationToken);
                    }

                    job.ReportProgress(local.Position);
                }

                if (job.BytesTransferred - lastPersisted >= PersistenceInterval)
                {
                    await local.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    lastPersisted = job.BytesTransferred;
                }
            }

            await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (local is FileStream fileStream)
            {
                fileStream.Flush(flushToDisk: true);
            }
            else
            {
                local.Flush();
            }
        }

        if (job.BytesTransferred != job.TotalBytes)
        {
            throw new IOException(
                $"The phone stopped sending data at {job.BytesTransferred} of {job.TotalBytes} bytes.");
        }

        var sha256 = await VerifyAsync(job.PartialPath, job, cancellationToken).ConfigureAwait(false);
        if (sha256 is not null &&
            await CompleteDuplicateConflictAsync(job, sha256, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await CommitCompletionAsync(
            job,
            sha256,
            publishFinalFile: () => _fileSystem.Move(job.PartialPath, job.DestinationPath))
            .ConfigureAwait(false);
    }

    private async Task CompleteExistingDestinationAsync(
        TransferJob job,
        CancellationToken cancellationToken)
    {
        var length = _fileSystem.GetFileLength(job.DestinationPath);
        if (length != job.TotalBytes)
        {
            throw new TransferPermanentException(
                "The destination already exists with a different size.");
        }

        var sha256 = await VerifyAsync(job.DestinationPath, job, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            job.ReportProgress(job.TotalBytes);
        }

        await CommitCompletionAsync(job, sha256).ConfigureAwait(false);
    }

    private async Task<string?> VerifyAsync(
        string path,
        TransferJob job,
        CancellationToken cancellationToken)
    {
        if (!_options.ComputeSha256 &&
            job.ExpectedSha256 is null &&
            job.RemoteEntityTag is null)
        {
            return null;
        }

        await using var stream = _fileSystem.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        if (job.ExpectedSha256 is not null &&
            !string.Equals(job.ExpectedSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new TransferPermanentException("The transferred file failed SHA-256 verification.");
        }

        if (job.RemoteEntityTag is not null)
        {
            var expectedFromEntityTag = ParseSha256EntityTag(job.RemoteEntityTag);
            if (expectedFromEntityTag is null ||
                !string.Equals(expectedFromEntityTag, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new TransferPermanentException(
                    "The transferred bytes do not match the remote entity validator.");
            }
        }

        return sha256;
    }

    private static string? ParseSha256EntityTag(string entityTag)
    {
        const string prefix = "\"sha256-";
        return entityTag.StartsWith(prefix, StringComparison.Ordinal) &&
            entityTag.EndsWith('"') &&
            entityTag.Length == prefix.Length + 64 + 1
                ? entityTag[prefix.Length..^1]
                : null;
    }

    private async Task RegisterLocalCopyAsync(
        TransferJob job,
        string? sha256,
        CancellationToken cancellationToken)
    {
        if (_localCopies is null)
        {
            return;
        }

        await _localCopies.RegisterAsync(
            new LocalCopy(
                job.DeviceId,
                job.RemoteId,
                job.DestinationPath,
                job.TotalBytes,
                _timeProvider.GetUtcNow(),
                sha256,
                job.RemoteModifiedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CompleteDuplicateConflictAsync(
        TransferJob job,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (_localCopies is null)
        {
            return false;
        }

        var existing = await _localCopies.FindByIdentityAsync(
            job.DeviceId,
            job.RemoteId,
            cancellationToken).ConfigureAwait(false);
        if (existing is null ||
            string.Equals(
                existing.LocalPath,
                job.DestinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existingSha256 = existing.Sha256;
        if (existingSha256 is null)
        {
            await using var stream = _fileSystem.OpenRead(existing.LocalPath);
            existingSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
        }

        if (!string.Equals(existingSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await CommitCompletionAsync(
            job,
            sha256,
            existing.LocalPath,
            () => _fileSystem.Delete(job.PartialPath)).ConfigureAwait(false);
        return true;
    }

    private async Task CommitCompletionAsync(
        TransferJob job,
        string? sha256,
        string? existingDestinationPath = null,
        Action? publishFinalFile = null)
    {
        TransferJob completed;
        lock (_sync)
        {
            completed = TransferJob.Restore(job.ToSnapshot());
            if (existingDestinationPath is null)
            {
                completed.Complete(sha256);
            }
            else
            {
                completed.CompleteUsingExistingCopy(existingDestinationPath, sha256!);
            }

            _committingTransfers.Add(job.Id);
        }

        try
        {
            publishFinalFile?.Invoke();
            await RegisterLocalCopyAsync(completed, sha256, CancellationToken.None)
                .ConfigureAwait(false);
            await _store.SaveAsync(completed, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                if (existingDestinationPath is null)
                {
                    job.Complete(sha256);
                }
                else
                {
                    job.CompleteUsingExistingCopy(existingDestinationPath, sha256!);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _committingTransfers.Remove(job.Id);
            }
        }
    }

    private async Task<TransferJob> AddCompletedDuplicateAsync(
        MediaItem media,
        LocalCopy copy,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var existing = _jobs.Values.FirstOrDefault(job =>
                job.Status != TransferStatus.Cancelled &&
                job.DeviceId == media.DeviceId &&
                job.RemoteId == media.RemoteId &&
                job.TotalBytes == media.FileSize &&
                SameInstant(job.RemoteModifiedAt, media.ModifiedAt));
            if (existing is not null)
            {
                return TransferJob.Restore(existing.ToSnapshot());
            }
        }

        var job = new TransferJob(
            Guid.NewGuid(),
            media.DeviceId,
            media.RemoteId,
            copy.LocalPath,
            media.FileSize,
            media.ModifiedAt,
            copy.Sha256);
        job.Start();
        job.ReportProgress(media.FileSize);
        job.Complete(copy.Sha256);
        lock (_sync)
        {
            var existingJob = _jobs.Values.FirstOrDefault(candidate =>
                candidate.Status != TransferStatus.Cancelled &&
                candidate.DeviceId == media.DeviceId &&
                candidate.RemoteId == media.RemoteId &&
                candidate.TotalBytes == media.FileSize &&
                SameInstant(candidate.RemoteModifiedAt, media.ModifiedAt));
            if (existingJob is not null)
            {
                return TransferJob.Restore(existingJob.ToSnapshot());
            }

            _jobs.Add(job.Id, job);
        }

        await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return TransferJob.Restore(job.ToSnapshot());
    }

    private static bool SameInstant(DateTimeOffset? left, DateTimeOffset right) =>
        left is DateTimeOffset value && Math.Abs((value - right).TotalSeconds) < 1;

    private async Task HandleFailureAsync(
        TransferJob job,
        Exception exception,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (job.Status != TransferStatus.Running)
            {
                return;
            }

            var reason = DescribeFailure(exception);
            if (IsPermanent(exception) || job.AttemptCount > _options.MaxRetries)
            {
                job.Fail(reason);
            }
            else
            {
                var exponent = Math.Min(job.AttemptCount - 1, 20);
                var delay = TimeSpan.FromTicks(_options.InitialRetryDelay.Ticks * (1L << exponent));
                job.ScheduleRetry(reason, _timeProvider.GetUtcNow() + delay);
            }
        }

        try
        {
            await _store.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The already persisted Running state is safely recovered on next startup.
        }

        SignalWorkers();
    }

    private static bool IsPermanent(Exception exception) =>
        exception is TransferPermanentException or UnauthorizedAccessException ||
        exception is MediaSourceProtocolException ||
        exception is MediaSourceHttpException
        {
            StatusCode: HttpStatusCode.NotFound or
                HttpStatusCode.Forbidden or
                HttpStatusCode.RequestedRangeNotSatisfiable
        } ||
        exception is IOException ioException && IsDiskFull(ioException);

    private static bool IsDiskFull(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 0x27 or 0x70;
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Windows denied access to the destination.",
        IOException ioException when IsDiskFull(ioException) => "The destination disk is full.",
        MediaSourceConnectionException => "The phone is offline or unreachable.",
        MediaSourceTimeoutException => "The phone did not respond before the request timed out.",
        _ => exception.Message,
    };

    private string GetAvailableDestinationPath(string directory, string fileName)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        lock (_sync)
        {
            for (var index = 0; ; index++)
            {
                var candidateName = index == 0 ? fileName : $"{stem} ({index}){extension}";
                var candidate = Path.Combine(fullDirectory, candidateName);
                if (!_fileSystem.FileExists(candidate) &&
                    !_fileSystem.FileExists($"{candidate}.partial") &&
                    _jobs.Values.All(job =>
                        !string.Equals(job.DestinationPath, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
        }
    }

    private TransferJob GetJob(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job)
            ? job
            : throw new KeyNotFoundException($"Transfer job {jobId} does not exist.");

    private void CancelActiveTransfer(Guid jobId)
    {
        if (_activeTransfers.TryGetValue(jobId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    private void SignalWorkers()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending signal is sufficient.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown has already completed.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TransferPermanentException(string message) : Exception(message);
}
