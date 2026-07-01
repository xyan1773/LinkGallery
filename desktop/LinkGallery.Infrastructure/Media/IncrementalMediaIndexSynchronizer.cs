using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Media;

public sealed class IncrementalMediaIndexSynchronizer : IMediaIndexSynchronizer
{
    private readonly SqliteMediaIndex _index;
    private readonly int _pageSize;

    public IncrementalMediaIndexSynchronizer(SqliteMediaIndex index, int pageSize = 200)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _index = index;
        _pageSize = pageSize;
    }

    public async Task<MediaSyncResult> SynchronizeAsync(
        IReadOnlyMediaSource source,
        CancellationToken cancellationToken) =>
        await SynchronizeAsync(source, progress: null, cancellationToken).ConfigureAwait(false);

    public async Task<MediaSyncResult> SynchronizeAsync(
        IReadOnlyMediaSource source,
        IProgress<MediaSyncProgress>? progress,
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
        var device = await source.GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
        await _index.UpsertDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        progress?.Report(new MediaSyncProgress(
            MediaSyncStage.DeviceLoaded,
            device,
            PagesFetched: 0,
            ItemsReceived: 0,
            TotalItems: device.MediaCount,
            ItemsRemoved: 0,
            WasFullScan: false));
        var checkpoint = await _index.GetCheckpointAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var localCount = await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var fullScan = checkpoint is null || device.MediaCount < localCount;
        var result = await ScanAsync(source, device, checkpoint, fullScan, progress, cancellationToken)
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

    private async Task<ScanResult> ScanAsync(
        IReadOnlyMediaSource source,
        Device device,
        SyncCheckpoint? checkpoint,
        bool fullScan,
        IProgress<MediaSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var seenAt = DateTimeOffset.UtcNow;
        var cursor = (string?)null;
        SyncCheckpoint? newHead = null;
        var pages = 0;
        var received = 0;
        var reachedCheckpoint = false;
        do
        {
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
