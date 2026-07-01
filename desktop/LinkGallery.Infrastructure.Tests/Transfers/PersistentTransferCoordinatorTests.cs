using System.Security.Cryptography;
using LinkGallery.Application.Media;
using LinkGallery.Application.Transfers;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;
using LinkGallery.Infrastructure.Media;
using LinkGallery.Infrastructure.Transfers;

namespace LinkGallery.Infrastructure.Tests.Transfers;

[TestClass]
public sealed class PersistentTransferCoordinatorTests
{
    [TestMethod]
    public async Task CompletedIsNotVisibleUntilTerminalSnapshotIsPersisted()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new BlockingTerminalStore();
            var source = new RecordingMediaSource(new byte[1024], TimeSpan.Zero);
            var queue = new PersistentTransferCoordinator(
                store,
                new StaticSourceResolver(source),
                new TransferCoordinatorOptions { MaxConcurrentTransfers = 1 });
            try
            {
                await queue.StartAsync();
                await queue.EnqueueAsync(CreateMedia(1024), directory);
                await store.TerminalSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.AreEqual(TransferStatus.Running, queue.GetJobs().Single().Status);
                var dispose = queue.DisposeAsync().AsTask();
                await Task.Delay(50);
                Assert.IsFalse(dispose.IsCompleted);

                store.AllowTerminalSave();
                await dispose;

                Assert.AreEqual(
                    TransferStatus.Completed,
                    (await store.LoadAsync()).Single().Status);
            }
            finally
            {
                store.AllowTerminalSave();
                await queue.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MoveStageCannotBePausedOrCancelled(bool cancel)
    {
        var directory = CreateTemporaryDirectory();
        var fileSystem = new BlockingMoveFileSystem();
        var source = new RecordingMediaSource(new byte[1024], TimeSpan.Zero);
        await using var queue = CreateQueue(
            Path.Combine(directory, "queue.json"),
            source,
            fileSystem: fileSystem);
        try
        {
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(CreateMedia(1024), directory);
            await fileSystem.MoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            if (cancel)
            {
                await queue.CancelAsync(job.Id);
            }
            else
            {
                await queue.PauseAllAsync();
            }

            Assert.AreEqual(TransferStatus.Running, queue.GetJobs().Single().Status);
            Assert.IsFalse(File.Exists(job.DestinationPath));

            fileSystem.AllowMove();
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == TransferStatus.Completed);

            Assert.IsTrue(File.Exists(job.DestinationPath));
            Assert.IsFalse(File.Exists(job.PartialPath));
        }
        finally
        {
            fileSystem.AllowMove();
            await queue.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RepeatedSelectionUsesOneTransferAndRegistersLocalCopy()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[1024 * 1024];
            RandomNumberGenerator.Fill(data);
            var source = new RecordingMediaSource(data, TimeSpan.FromMilliseconds(2));
            using var copies = new LocalCopyCatalog(Path.Combine(directory, "copies.json"));
            await using var queue = CreateQueue(
                Path.Combine(directory, "queue.json"),
                source,
                localCopies: copies);
            await queue.StartAsync();
            var media = CreateMedia(data.Length);

            var first = await queue.EnqueueAsync(media, directory);
            var repeated = await queue.EnqueueAsync(media, directory);

            Assert.AreEqual(first.Id, repeated.Id);
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == TransferStatus.Completed);
            var localCopy = await copies.FindAsync(media);
            Assert.IsNotNull(localCopy);
            Assert.AreEqual(first.DestinationPath, localCopy.LocalPath);
            Assert.AreEqual(1, source.RequestedOffsets.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DifferentMediaWithSameNameAreBothKept()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[64 * 1024];
            var source = new RecordingMediaSource(data, TimeSpan.Zero);
            await using var queue = CreateQueue(Path.Combine(directory, "queue.json"), source);
            await queue.StartAsync();
            var firstMedia = CreateMedia(data.Length);
            var secondMedia = new MediaItem
            {
                DeviceId = firstMedia.DeviceId,
                RemoteId = "different-media",
                FileName = firstMedia.FileName,
                Type = firstMedia.Type,
                FileSize = firstMedia.FileSize,
                TakenAt = firstMedia.TakenAt,
                ModifiedAt = firstMedia.ModifiedAt,
            };

            var first = await queue.EnqueueAsync(firstMedia, directory);
            var second = await queue.EnqueueAsync(secondMedia, directory);
            await WaitUntilAsync(
                () => queue.GetJobs().All(job => job.Status == TransferStatus.Completed));

            Assert.AreNotEqual(first.DestinationPath, second.DestinationPath);
            Assert.IsTrue(File.Exists(first.DestinationPath));
            Assert.IsTrue(File.Exists(second.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MetadataConflictFallsBackToSha256AndReusesIdenticalCopy()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[64 * 1024];
            RandomNumberGenerator.Fill(data);
            var existingPath = Path.Combine(directory, "existing.bin");
            await File.WriteAllBytesAsync(existingPath, data);
            var media = CreateMedia(data.Length);
            using var copies = new LocalCopyCatalog(Path.Combine(directory, "copies.json"));
            await copies.RegisterAsync(new LocalCopy(
                media.DeviceId,
                media.RemoteId,
                existingPath,
                media.FileSize,
                DateTimeOffset.UtcNow,
                RemoteModifiedAt: media.ModifiedAt.AddMinutes(-1)));
            var source = new RecordingMediaSource(data, TimeSpan.Zero);
            await using var queue = CreateQueue(
                Path.Combine(directory, "queue.json"),
                source,
                localCopies: copies);
            await queue.StartAsync();

            var queued = await queue.EnqueueAsync(media, directory);
            await WaitUntilAsync(
                () => queue.GetJobs().Single().Status == TransferStatus.Completed);
            var completed = queue.GetJobs().Single();

            Assert.AreEqual(existingPath, completed.DestinationPath);
            Assert.IsFalse(File.Exists(queued.PartialPath));
            Assert.AreEqual(1, Directory.GetFiles(directory, "*.bin").Length);
            Assert.AreEqual(media.ModifiedAt, (await copies.FindAsync(media))?.RemoteModifiedAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ClearingCompletedJobsAlsoRemovesPersistedQueueEntries()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var storePath = Path.Combine(directory, "queue.json");
            var source = new RecordingMediaSource(new byte[1024], TimeSpan.Zero);
            await using (var queue = CreateQueue(storePath, source))
            {
                await queue.StartAsync();
                await queue.EnqueueAsync(CreateMedia(1024), directory);
                await WaitUntilAsync(
                    () => queue.GetJobs().Single().Status == TransferStatus.Completed);
                await queue.ClearCompletedAsync();
                Assert.IsEmpty(queue.GetJobs());
            }

            await using var restarted = CreateQueue(storePath, source);
            await restarted.StartAsync();
            Assert.IsEmpty(restarted.GetJobs());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingCompletedFileBecomesRetryableAfterRestart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var storePath = Path.Combine(directory, "queue.json");
            var source = new RecordingMediaSource(new byte[1024], TimeSpan.Zero);
            string destinationPath;
            await using (var queue = CreateQueue(storePath, source))
            {
                await queue.StartAsync();
                var job = await queue.EnqueueAsync(CreateMedia(1024), directory);
                destinationPath = job.DestinationPath;
                await WaitUntilAsync(
                    () => queue.GetJobs().Single().Status == TransferStatus.Completed);
            }

            File.Delete(destinationPath);
            await using var restarted = CreateQueue(storePath, source);
            await restarted.StartAsync();

            Assert.AreEqual(TransferStatus.Failed, restarted.GetJobs().Single().Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LargeFileResumesAfterQueueRestartAndIsAtomicallyCommitted()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[8 * 1024 * 1024];
            RandomNumberGenerator.Fill(data);
            var source = new RecordingMediaSource(data, delayEachRead: TimeSpan.FromMilliseconds(2));
            var storePath = Path.Combine(directory, "queue.json");
            Guid jobId;

            await using (var firstQueue = CreateQueue(storePath, source))
            {
                await firstQueue.StartAsync();
                var job = await firstQueue.EnqueueAsync(CreateMedia(data.Length), directory);
                jobId = job.Id;
                await WaitUntilAsync(
                    () => File.Exists(job.PartialPath) &&
                        new FileInfo(job.PartialPath).Length >= 512 * 1024);
            }

            var bytesBeforeRestart = new FileInfo(
                Path.Combine(directory, "large.bin.partial")).Length;
            Assert.IsGreaterThan(0, bytesBeforeRestart);

            await using (var secondQueue = CreateQueue(storePath, source))
            {
                await secondQueue.StartAsync();
                await WaitUntilAsync(
                    () => secondQueue.GetJobs().Single(job => job.Id == jobId).Status ==
                        TransferStatus.Completed,
                    TimeSpan.FromSeconds(20));
            }

            var finalPath = Path.Combine(directory, "large.bin");
            Assert.IsTrue(File.Exists(finalPath));
            Assert.IsFalse(File.Exists($"{finalPath}.partial"));
            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(finalPath));
            Assert.IsTrue(source.RequestedOffsets.Skip(1).Any(offset => offset >= bytesBeforeRestart));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task HashMismatchFailsAndLeavesOnlyPartialFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[1024 * 1024];
            RandomNumberGenerator.Fill(data);
            var source = new RecordingMediaSource(data, TimeSpan.Zero);
            await using var queue = CreateQueue(Path.Combine(directory, "queue.json"), source);
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(
                CreateMedia(data.Length),
                directory,
                new string('0', 64));

            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Failed);

            Assert.IsTrue(File.Exists(job.PartialPath));
            Assert.IsFalse(File.Exists(job.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CopiesReal128MiBFileFromDisk()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "source-large.bin");
            await using (var sourceFile = new FileStream(
                sourcePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                sourceFile.SetLength(128L * 1024 * 1024);
                sourceFile.Position = sourceFile.Length - 1;
                await sourceFile.WriteAsync(new byte[] { 0x5a });
            }

            var source = new FileMediaSource(sourcePath);
            var destinationDirectory = Path.Combine(directory, "destination");
            await using var queue = CreateQueue(Path.Combine(directory, "queue.json"), source);
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(
                CreateMedia(new FileInfo(sourcePath).Length),
                destinationDirectory);

            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Completed,
                TimeSpan.FromSeconds(30));

            await using var sourceRead = File.OpenRead(sourcePath);
            await using var destinationRead = File.OpenRead(job.DestinationPath);
            CollectionAssert.AreEqual(
                await SHA256.HashDataAsync(sourceRead),
                await SHA256.HashDataAsync(destinationRead));
            Assert.AreEqual(128L * 1024 * 1024, destinationRead.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InterruptedStreamRetriesWithIfRangeFromPersistedOffset()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[2 * 1024 * 1024];
            RandomNumberGenerator.Fill(data);
            var source = new InterruptingMediaSource(data, interruptAfter: 512 * 1024);
            await using var queue = CreateQueue(
                Path.Combine(directory, "queue.json"),
                source,
                retryDelay: TimeSpan.FromMilliseconds(300));
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(CreateMedia(data.Length), directory);

            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Retrying);
            var retrying = queue.GetJobs().Single(candidate => candidate.Id == job.Id);
            Assert.IsNotNull(retrying.RetryAfter);
            Assert.IsTrue(retrying.RetryAfter > DateTimeOffset.UtcNow);

            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Completed);

            var completed = queue.GetJobs().Single(candidate => candidate.Id == job.Id);
            Assert.AreEqual(2, completed.AttemptCount);
            Assert.IsTrue(source.RequestedOffsets.Skip(1).Any(offset => offset > 0));
            Assert.IsTrue(source.IfRangeValues.Skip(1).All(value => value == source.EntityTag));
            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(job.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PauseAndResumeStopsAndContinuesActiveIo()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[4 * 1024 * 1024];
            var source = new RecordingMediaSource(data, TimeSpan.FromMilliseconds(5));
            await using var queue = CreateQueue(Path.Combine(directory, "queue.json"), source);
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(CreateMedia(data.Length), directory);
            await WaitUntilAsync(() => File.Exists(job.PartialPath));

            await queue.PauseAsync(job.Id);
            await Task.Delay(100);

            Assert.AreEqual(
                TransferStatus.Paused,
                queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status);
            Assert.IsFalse(File.Exists(job.DestinationPath));

            await queue.ResumeAsync(job.Id);
            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Completed);
            Assert.IsTrue(File.Exists(job.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancelStopsActiveIoWithoutPublishingDestination()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[4 * 1024 * 1024];
            var source = new RecordingMediaSource(data, TimeSpan.FromMilliseconds(5));
            await using var queue = CreateQueue(Path.Combine(directory, "queue.json"), source);
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(CreateMedia(data.Length), directory);
            await WaitUntilAsync(() => File.Exists(job.PartialPath));

            await queue.CancelAsync(job.Id);
            await Task.Delay(100);

            Assert.AreEqual(
                TransferStatus.Cancelled,
                queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status);
            Assert.IsFalse(File.Exists(job.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, "disk is full")]
    [DataRow(true, "denied access")]
    public async Task PermanentDestinationFailuresDoNotRetry(
        bool permissionDenied,
        string expectedReason)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[1024];
            var source = new RecordingMediaSource(data, TimeSpan.Zero);
            var fileSystem = new FailingWriteFileSystem(permissionDenied);
            await using var queue = CreateQueue(
                Path.Combine(directory, "queue.json"),
                source,
                fileSystem: fileSystem);
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(CreateMedia(data.Length), directory);

            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Failed);

            var failed = queue.GetJobs().Single(candidate => candidate.Id == job.Id);
            Assert.AreEqual(1, failed.AttemptCount);
            StringAssert.Contains(failed.FailureReason, expectedReason);
            Assert.IsFalse(File.Exists(job.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteSizeChangeFailsWithoutPublishingDestination()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[1024];
            var source = new RecordingMediaSource(
                data,
                TimeSpan.Zero,
                reportedLength: data.Length + 1);
            await using var queue = CreateQueue(Path.Combine(directory, "queue.json"), source);
            await queue.StartAsync();
            var job = await queue.EnqueueAsync(CreateMedia(data.Length), directory);

            await WaitUntilAsync(
                () => queue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Failed);

            Assert.IsFalse(File.Exists(job.DestinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistedRunningJobIsRecoveredWithoutIllegalTransition()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "queue.json");
            using (var store = new JsonTransferJobStore(path))
            {
                var job = new TransferJob(
                    Guid.NewGuid(),
                    "phone",
                    "media",
                    Path.Combine(directory, "photo.jpg"),
                    100);
                job.Start();
                job.ReportProgress(40);
                await store.SaveAsync(job);
            }

            using var reloadedStore = new JsonTransferJobStore(path);
            var restoredJobs = await reloadedStore.LoadAsync();
            Assert.HasCount(1, restoredJobs);
            var restored = restoredJobs[0];
            restored.RecoverAfterRestart();

            Assert.AreEqual(TransferStatus.Pending, restored.Status);
            Assert.AreEqual(40, restored.BytesTransferred);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task KilledProcessStateResumesWithoutGracefulShutdown()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var data = new byte[2 * 1024 * 1024];
            RandomNumberGenerator.Fill(data);
            var source = new RecordingMediaSource(data, TimeSpan.Zero);
            var storePath = Path.Combine(directory, "queue.json");
            var destinationPath = Path.Combine(directory, "large.bin");
            var job = new TransferJob(
                Guid.NewGuid(),
                "phone",
                "large-media",
                destinationPath,
                data.Length);
            job.Start();
            job.CaptureRemoteEntityTag(source.EntityTag);
            const int persistedLength = 512 * 1024;
            job.ReportProgress(persistedLength);
            await File.WriteAllBytesAsync(job.PartialPath, data[..persistedLength]);
            using (var abandonedProcessStore = new JsonTransferJobStore(storePath))
            {
                await abandonedProcessStore.SaveAsync(job);
            }

            await using var recoveredQueue = CreateQueue(storePath, source);
            await recoveredQueue.StartAsync();
            await WaitUntilAsync(
                () => recoveredQueue.GetJobs().Single(candidate => candidate.Id == job.Id).Status ==
                    TransferStatus.Completed);

            Assert.AreEqual(persistedLength, source.RequestedOffsets[0]);
            CollectionAssert.AreEqual(data, await File.ReadAllBytesAsync(destinationPath));
            Assert.IsFalse(File.Exists(job.PartialPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PersistentTransferCoordinator CreateQueue(
        string storePath,
        IReadOnlyMediaSource source,
        TimeSpan? retryDelay = null,
        ITransferFileSystem? fileSystem = null,
        LocalCopyCatalog? localCopies = null) =>
        new(
            new JsonTransferJobStore(storePath),
            new StaticSourceResolver(source),
            new TransferCoordinatorOptions
            {
                MaxConcurrentTransfers = 1,
                InitialRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(10),
            },
            fileSystem: fileSystem,
            localCopies: localCopies);

    private static MediaItem CreateMedia(long length) =>
        new()
        {
            DeviceId = "phone",
            RemoteId = "large-media",
            FileName = "large.bin",
            Type = MediaType.Video,
            FileSize = length,
            TakenAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinkGallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, cancellation.Token);
        }
    }

    private sealed class StaticSourceResolver(IReadOnlyMediaSource source) : ITransferMediaSourceResolver
    {
        public ValueTask<IReadOnlyMediaSource> ResolveAsync(
            string deviceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(source);
    }

    private sealed class BlockingTerminalStore : ITransferJobStore
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _allowTerminalSave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TransferJob? _saved;

        public TaskCompletionSource TerminalSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TransferJob>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                IReadOnlyList<TransferJob> jobs = _saved is null
                    ? []
                    : [TransferJob.Restore(_saved.ToSnapshot())];
                return Task.FromResult(jobs);
            }
        }

        public async Task SaveAsync(
            TransferJob job,
            CancellationToken cancellationToken = default)
        {
            if (job.Status == TransferStatus.Completed)
            {
                TerminalSaveStarted.TrySetResult();
                await _allowTerminalSave.Task.ConfigureAwait(false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            lock (_sync)
            {
                _saved = TransferJob.Restore(job.ToSnapshot());
            }
        }

        public Task DeleteAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_saved?.Id == jobId)
                {
                    _saved = null;
                }
            }

            return Task.CompletedTask;
        }

        public void AllowTerminalSave() => _allowTerminalSave.TrySetResult();
    }

    private sealed class BlockingMoveFileSystem : ITransferFileSystem
    {
        private readonly TaskCompletionSource _allowMove =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource MoveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FileExists(string path) => File.Exists(path);

        public long GetFileLength(string path) => new FileInfo(path).Length;

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public Stream OpenPartialWrite(string path, long length)
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.SetLength(length);
            stream.Position = length;
            return stream;
        }

        public Stream OpenRead(string path) => File.OpenRead(path);

        public void Move(string sourcePath, string destinationPath)
        {
            MoveStarted.TrySetResult();
            _allowMove.Task.GetAwaiter().GetResult();
            File.Move(sourcePath, destinationPath);
        }

        public void Delete(string path) => File.Delete(path);

        public void AllowMove() => _allowMove.TrySetResult();
    }

    private sealed class RecordingMediaSource(
        byte[] data,
        TimeSpan delayEachRead,
        long? reportedLength = null)
        : IReadOnlyMediaSource, IEntityAwareMediaSource
    {
        private readonly List<long> _requestedOffsets = [];
        public string EntityTag { get; } =
            $"\"sha256-{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}\"";

        public IReadOnlyList<long> RequestedOffsets
        {
            get
            {
                lock (_requestedOffsets)
                {
                    return _requestedOffsets.ToArray();
                }
            }
        }

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken)
            => OpenOriginalAsync(remoteId, offset, entityTag: null, cancellationToken);

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            if (entityTag is not null &&
                !string.Equals(entityTag, EntityTag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stale entity tag.");
            }

            lock (_requestedOffsets)
            {
                _requestedOffsets.Add(offset);
            }

            Stream stream = new DelayedMemoryStream(
                data,
                checked((int)offset),
                delayEachRead,
                reportedLength ?? data.Length,
                EntityTag);
            return Task.FromResult(stream);
        }

        public Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DelayedMemoryStream(
        byte[] data,
        int offset,
        TimeSpan delayEachRead,
        long reportedLength,
        string entityTag)
        : MemoryStream(data, offset, data.Length - offset, writable: false), IRemoteMediaStreamMetadata
    {
        public long? TotalLength => reportedLength;

        public DateTimeOffset? LastModified => null;

        public string? EntityTag => entityTag;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (delayEachRead > TimeSpan.Zero)
            {
                await Task.Delay(delayEachRead, cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class InterruptingMediaSource(byte[] data, int interruptAfter)
        : IReadOnlyMediaSource, IEntityAwareMediaSource
    {
        private int _openCount;

        public string EntityTag { get; } =
            $"\"sha256-{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}\"";

        public List<long> RequestedOffsets { get; } = [];

        public List<string?> IfRangeValues { get; } = [];

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken) =>
            OpenOriginalAsync(remoteId, offset, entityTag: null, cancellationToken);

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            RequestedOffsets.Add(offset);
            IfRangeValues.Add(entityTag);
            var shouldInterrupt = Interlocked.Increment(ref _openCount) == 1;
            Stream stream = new InterruptingMemoryStream(
                data,
                checked((int)offset),
                EntityTag,
                shouldInterrupt ? interruptAfter : null,
                data.Length);
            return Task.FromResult(stream);
        }

        public Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class InterruptingMemoryStream(
        byte[] data,
        int offset,
        string entityTag,
        int? interruptAfter,
        long totalLength)
        : MemoryStream(data, offset, data.Length - offset, writable: false), IRemoteMediaStreamMetadata
    {
        private int _bytesRead;

        public long? TotalLength => totalLength;

        public DateTimeOffset? LastModified => null;

        public string? EntityTag => entityTag;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (interruptAfter is int limit && _bytesRead >= limit)
            {
                throw new IOException("Simulated network interruption.");
            }

            var allowed = interruptAfter is int remainingLimit
                ? Math.Min(buffer.Length, remainingLimit - _bytesRead)
                : buffer.Length;
            var read = base.Read(buffer.Span[..allowed]);
            _bytesRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class FileMediaSource(string path) : IReadOnlyMediaSource, IEntityAwareMediaSource
    {
        private readonly long _length = new FileInfo(path).Length;
        private readonly string _entityTag = CreateEntityTag(path);

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            CancellationToken cancellationToken) =>
            OpenOriginalAsync(remoteId, offset, entityTag: null, cancellationToken);

        public Task<Stream> OpenOriginalAsync(
            string remoteId,
            long offset,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            if (entityTag is not null &&
                !string.Equals(entityTag, _entityTag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stale entity tag.");
            }

            var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            file.Position = offset;
            Stream stream = new MetadataOwnedStream(file, _length, _entityTag);
            return Task.FromResult(stream);
        }

        public Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MediaPage> GetMediaPageAsync(
            MediaQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenThumbnailAsync(
            string remoteId,
            ThumbnailSize size,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static string CreateEntityTag(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return $"\"sha256-{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}\"";
        }
    }

    private sealed class MetadataOwnedStream(Stream inner, long totalLength, string entityTag)
        : Stream, IRemoteMediaStreamMetadata
    {
        public long? TotalLength => totalLength;

        public DateTimeOffset? LastModified => null;

        public string? EntityTag => entityTag;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingWriteFileSystem(bool permissionDenied) : ITransferFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);

        public long GetFileLength(string path) => new FileInfo(path).Length;

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public Stream OpenPartialWrite(string path, long length) =>
            throw (permissionDenied
                ? new UnauthorizedAccessException("Simulated access denial.")
                : new DiskFullIOException());

        public Stream OpenRead(string path) => File.OpenRead(path);

        public void Move(string sourcePath, string destinationPath) =>
            File.Move(sourcePath, destinationPath);

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class DiskFullIOException : IOException
    {
        public DiskFullIOException()
            : base("Simulated disk full.")
        {
            HResult = unchecked((int)0x80070070);
        }
    }
}
