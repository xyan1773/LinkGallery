using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Media;

public sealed class IncrementalMediaIndexSynchronizer : IMediaIndexSynchronizer
{
    private readonly SqliteMediaIndex _index;
    private readonly int _pageSize;
    private readonly BackgroundIndexGate _gate;

    public IncrementalMediaIndexSynchronizer(
        SqliteMediaIndex index,
        int pageSize = 200,
        BackgroundIndexGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _index = index;
        _pageSize = pageSize;
        _gate = gate ?? new BackgroundIndexGate();
    }

    public async Task<MediaSyncResult> SynchronizeAsync(
        IReadOnlyMediaSource source,
        CancellationToken cancellationToken) =>
        await SynchronizeAsync(source, progress: null, cancellationToken).ConfigureAwait(false);

    public async Task<MediaSyncResult> SynchronizeAsync(
        IReadOnlyMediaSource source,
        IProgress<MediaSyncProgress>? progress,
        CancellationToken cancellationToken) =>
        await SynchronizeAsync(source, progress, seed: null, cancellationToken).ConfigureAwait(false);

    public async Task<MediaSyncResult> SynchronizeAsync(
        IReadOnlyMediaSource source,
        IProgress<MediaSyncProgress>? progress,
        MediaSyncSeed? seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        progress?.Report(new MediaSyncProgress(
            MediaSyncStage.Connecting,
            Device: null,
            PagesFetched: 0,
            ItemsReceived: 0,
            TotalItems: null,
            ItemsRemoved: 0,
            WasFullScan: false));
        await WaitForGateAsync(
            progress,
            seed?.Device,
            0,
            seed?.FirstPage.Items.Count ?? 0,
            seed?.Device.MediaCount,
            wasFullScan: false,
            cancellationToken).ConfigureAwait(false);
        var device = seed?.Device ??
            await source.GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
        await _index.UpsertDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        progress?.Report(new MediaSyncProgress(
            MediaSyncStage.DeviceLoaded,
            device,
            PagesFetched: 0,
            ItemsReceived: 0,
            TotalItems: device.MediaCount,
            ItemsRemoved: 0,
            WasFullScan: false));

        if (source is IIncrementalMediaSource incrementalSource)
        {
            var incrementalResult = await SynchronizeIncrementallyAsync(
                source,
                incrementalSource,
                device,
                progress,
                seed,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.Completed,
                device,
                incrementalResult.PagesFetched,
                incrementalResult.ItemsReceived,
                device.MediaCount,
                incrementalResult.ItemsRemoved,
                incrementalResult.WasFullScan));
            return new MediaSyncResult(
                device,
                incrementalResult.PagesFetched,
                incrementalResult.ItemsReceived,
                incrementalResult.ItemsRemoved,
                incrementalResult.WasFullScan);
        }

        var checkpoint = await _index.GetCheckpointAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var localCount = await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var fullScan = checkpoint is null || device.MediaCount < localCount;
        var result = await ScanAsync(
                source,
                device,
                checkpoint,
                fullScan,
                progress,
                fullScan ? seed : null,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.WasFullScan &&
            await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false) > device.MediaCount)
        {
            var reconciliation = await ScanAsync(
                source,
                device,
                checkpoint: null,
                fullScan: true,
                progress,
                seed: null,
                cancellationToken).ConfigureAwait(false);
            result = new ScanResult(
                result.PagesFetched + reconciliation.PagesFetched,
                result.ItemsReceived + reconciliation.ItemsReceived,
                reconciliation.ItemsRemoved,
                WasFullScan: true);
        }

        progress?.Report(new MediaSyncProgress(
            MediaSyncStage.Completed,
            device,
            result.PagesFetched,
            result.ItemsReceived,
            device.MediaCount,
            result.ItemsRemoved,
            result.WasFullScan));
        return new MediaSyncResult(
            device,
            result.PagesFetched,
            result.ItemsReceived,
            result.ItemsRemoved,
            result.WasFullScan);
    }

    private async Task<ScanResult> SynchronizeIncrementallyAsync(
        IReadOnlyMediaSource source,
        IIncrementalMediaSource incrementalSource,
        Device device,
        IProgress<MediaSyncProgress>? progress,
        MediaSyncSeed? seed,
        CancellationToken cancellationToken)
    {
        await WaitForGateAsync(
            progress,
            device,
            0,
            seed?.FirstPage.Items.Count ?? 0,
            device.MediaCount,
            wasFullScan: false,
            cancellationToken).ConfigureAwait(false);
        var remoteState = seed?.BaselineState ??
            await incrementalSource.GetSyncStateAsync(cancellationToken).ConfigureAwait(false);
        var localState = await _index.GetDeviceSyncStateAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var localCount = await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var canApplyChanges =
            localState is
            {
                FullIndexCompleted: true,
                SyncCursor: not null,
                LibraryVersion: not null,
            } &&
            string.Equals(
                localState.LibraryVersion,
                remoteState.LibraryVersion,
                StringComparison.Ordinal) &&
            localCount <= remoteState.Total;

        if (canApplyChanges)
        {
            var incremental = await ApplyChangesAsync(
                incrementalSource,
                device,
                localState!.SyncCursor!,
                remoteState,
                progress,
                cancellationToken).ConfigureAwait(false);
            var reconciliation = await ReconcileManifestAsync(
                incrementalSource,
                device,
                remoteState,
                progress,
                cancellationToken).ConfigureAwait(false);
            incremental = new ScanResult(
                incremental.PagesFetched + reconciliation.PagesFetched,
                incremental.ItemsReceived,
                incremental.ItemsRemoved + reconciliation.ItemsRemoved,
                WasFullScan: false);
            if (await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false) == remoteState.Total)
            {
                return incremental;
            }
        }

        var full = await ScanAsync(
            source,
            device,
            checkpoint: null,
            fullScan: true,
            progress,
            seed,
            cancellationToken).ConfigureAwait(false);
        await _index.SaveDeviceSyncStateAsync(
            device.Id,
            remoteState,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        var latestState = await incrementalSource.GetSyncStateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                remoteState.LibraryVersion,
                latestState.LibraryVersion,
                StringComparison.Ordinal))
        {
            return full;
        }

        if (!string.Equals(
                remoteState.LatestCursor,
                latestState.LatestCursor,
                StringComparison.Ordinal))
        {
            var catchUp = await ApplyChangesAsync(
                incrementalSource,
                device,
                remoteState.LatestCursor,
                latestState,
                progress,
                cancellationToken).ConfigureAwait(false);
            full = new ScanResult(
                full.PagesFetched + catchUp.PagesFetched,
                full.ItemsReceived + catchUp.ItemsReceived,
                full.ItemsRemoved + catchUp.ItemsRemoved,
                WasFullScan: true);
        }

        if (await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false) != latestState.Total)
        {
            var reconciliation = await ScanAsync(
                source,
                device,
                checkpoint: null,
                fullScan: true,
                progress,
                seed: null,
                cancellationToken).ConfigureAwait(false);
            await _index.SaveDeviceSyncStateAsync(
                device.Id,
                latestState,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            full = new ScanResult(
                full.PagesFetched + reconciliation.PagesFetched,
                full.ItemsReceived + reconciliation.ItemsReceived,
                full.ItemsRemoved + reconciliation.ItemsRemoved,
                WasFullScan: true);
        }

        var manifest = await ReconcileManifestAsync(
            incrementalSource,
            device,
            latestState,
            progress,
            cancellationToken).ConfigureAwait(false);
        full = new ScanResult(
            full.PagesFetched + manifest.PagesFetched,
            full.ItemsReceived,
            full.ItemsRemoved + manifest.ItemsRemoved,
            WasFullScan: true);

        return full;
    }

    private async Task<ScanResult> ApplyChangesAsync(
        IIncrementalMediaSource source,
        Device device,
        string cursor,
        RemoteMediaSyncState expectedState,
        IProgress<MediaSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.Equals(cursor, expectedState.LatestCursor, StringComparison.Ordinal))
        {
            return new ScanResult(0, 0, 0, WasFullScan: false);
        }

        var pages = 0;
        var received = 0;
        var removed = 0;
        while (true)
        {
            await WaitForGateAsync(
                progress,
                device,
                pages,
                received,
                expectedState.Total,
                wasFullScan: false,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.FetchingPage,
                device,
                pages,
                received,
                expectedState.Total,
                removed,
                WasFullScan: false));
            var page = await source.GetChangesAsync(cursor, 500, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    page.LibraryVersion,
                    expectedState.LibraryVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(page.FromCursor, cursor, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Incremental media cursor or library version changed.");
            }

            await WaitForGateAsync(
                progress,
                device,
                pages,
                received,
                expectedState.Total,
                wasFullScan: false,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.WritingPage,
                device,
                pages + 1,
                received + page.Upserts.Count,
                expectedState.Total,
                removed,
                WasFullScan: false));
            removed += await _index.ApplyChangePageAsync(
                device.Id,
                page,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            pages++;
            received += page.Upserts.Count;
            if (!page.HasMore)
            {
                break;
            }
            if (string.Equals(page.NextCursor, cursor, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Incremental media cursor did not advance.");
            }
            cursor = page.NextCursor;
        }

        return new ScanResult(pages, received, removed, WasFullScan: false);
    }

    private async Task<ScanResult> ReconcileManifestAsync(
        IIncrementalMediaSource source,
        Device device,
        RemoteMediaSyncState expectedState,
        IProgress<MediaSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cursor = (string?)null;
        var pages = 0;
        var entries = new Dictionary<string, RemoteMediaManifestEntry>(StringComparer.Ordinal);
        while (true)
        {
            await WaitForGateAsync(
                progress,
                device,
                pages,
                entries.Count,
                expectedState.Total,
                wasFullScan: false,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.FetchingPage,
                device,
                pages,
                entries.Count,
                expectedState.Total,
                ItemsRemoved: 0,
                WasFullScan: false));
            var page = await source.GetManifestPageAsync(cursor, 500, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    page.LibraryVersion,
                    expectedState.LibraryVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Media manifest library version changed.");
            }
            foreach (var entry in page.Items)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !entries.TryAdd(entry.Id, entry))
                {
                    throw new InvalidDataException("Media manifest contains an invalid or duplicate ID.");
                }
            }
            pages++;
            if (!page.HasMore)
            {
                break;
            }
            if (page.NextCursor is null ||
                string.Equals(page.NextCursor, cursor, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Media manifest cursor did not advance.");
            }
            cursor = page.NextCursor;
        }

        if (entries.Count != expectedState.Total)
        {
            throw new InvalidDataException("Media manifest count does not match sync state.");
        }
        progress?.Report(new MediaSyncProgress(
            MediaSyncStage.Completing,
            device,
            pages,
            entries.Count,
            expectedState.Total,
            ItemsRemoved: 0,
            WasFullScan: false));
        var removed = await _index.ReconcileManifestAsync(
            device.Id,
            entries.Values.ToArray(),
            expectedState,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return new ScanResult(pages, 0, removed, WasFullScan: false);
    }

    private async Task<ScanResult> ScanAsync(
        IReadOnlyMediaSource source,
        Device device,
        SyncCheckpoint? checkpoint,
        bool fullScan,
        IProgress<MediaSyncProgress>? progress,
        MediaSyncSeed? seed,
        CancellationToken cancellationToken)
    {
        var seenAt = DateTimeOffset.UtcNow;
        var cursor = seed?.FirstPage.NextCursor;
        SyncCheckpoint? newHead = null;
        var pages = seed is null ? 0 : 1;
        var received = seed?.FirstPage.Items.Count ?? 0;
        var reachedCheckpoint = false;
        if (seed is not null)
        {
            await WaitForGateAsync(
                progress,
                device,
                0,
                0,
                device.MediaCount,
                fullScan,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(seed.Device.Id, device.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Initial media page belongs to a different device.");
            }
            if (seed.FirstPage.Items.Count > 0)
            {
                newHead = ToCheckpoint(seed.FirstPage.Items[0]);
                await _index.UpsertItemsAsync(
                    seed.FirstPage.Items,
                    seenAt,
                    cancellationToken).ConfigureAwait(false);
            }
            if (cursor is null)
            {
                goto ScanCompleted;
            }
        }
        do
        {
            await WaitForGateAsync(
                progress,
                device,
                pages,
                received,
                device.MediaCount,
                fullScan,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.FetchingPage,
                device,
                pages,
                received,
                device.MediaCount,
                ItemsRemoved: 0,
                WasFullScan: fullScan));
            var page = await source.GetMediaPageAsync(
                new MediaQuery(cursor, _pageSize),
                cancellationToken).ConfigureAwait(false);
            pages++;
            var items = page.Items;
            if (newHead is null && items.Count > 0)
            {
                newHead = ToCheckpoint(items[0]);
            }

            if (!fullScan && checkpoint is not null)
            {
                var checkpointIndex = FindCheckpoint(items, checkpoint);
                if (checkpointIndex >= 0)
                {
                    items = items.Take(checkpointIndex).ToArray();
                    reachedCheckpoint = true;
                }
            }

            await WaitForGateAsync(
                progress,
                device,
                pages,
                received,
                device.MediaCount,
                fullScan,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.WritingPage,
                device,
                pages,
                received + items.Count,
                device.MediaCount,
                ItemsRemoved: 0,
                WasFullScan: fullScan));
            await _index.UpsertItemsAsync(items, seenAt, cancellationToken).ConfigureAwait(false);
            received += items.Count;
            cursor = page.NextCursor;
            if (reachedCheckpoint)
            {
                break;
            }
        }
        while (cursor is not null);

    ScanCompleted:
        var scannedEntireLibrary = fullScan || !reachedCheckpoint;
        progress?.Report(new MediaSyncProgress(
            MediaSyncStage.Completing,
            device,
            pages,
            received,
            device.MediaCount,
            ItemsRemoved: 0,
            WasFullScan: scannedEntireLibrary));
        var removed = await _index.CompleteAsync(
            device.Id,
            newHead,
            seenAt,
            removeItemsNotSeen: scannedEntireLibrary,
            cancellationToken).ConfigureAwait(false);
        return new ScanResult(pages, received, removed, scannedEntireLibrary);
    }

    private async Task WaitForGateAsync(
        IProgress<MediaSyncProgress>? progress,
        Device? device,
        int pagesFetched,
        int itemsReceived,
        int? totalItems,
        bool wasFullScan,
        CancellationToken cancellationToken)
    {
        if (_gate.IsPaused)
        {
            progress?.Report(new MediaSyncProgress(
                MediaSyncStage.Paused,
                device,
                pagesFetched,
                itemsReceived,
                totalItems,
                ItemsRemoved: 0,
                WasFullScan: wasFullScan));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int FindCheckpoint(
        IReadOnlyList<MediaItem> items,
        SyncCheckpoint checkpoint)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].RemoteId == checkpoint.RemoteId &&
                items[index].ModifiedAt.ToUniversalTime() == checkpoint.ModifiedAt.ToUniversalTime())
            {
                return index;
            }
        }

        return -1;
    }

    private static SyncCheckpoint ToCheckpoint(MediaItem item) =>
        new(item.RemoteId, item.ModifiedAt);

    private sealed record ScanResult(
        int PagesFetched,
        int ItemsReceived,
        int ItemsRemoved,
        bool WasFullScan);
}
