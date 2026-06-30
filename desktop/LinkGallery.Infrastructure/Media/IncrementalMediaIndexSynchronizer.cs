using LinkGallery.Application.Media;
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var device = await source.GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
        await _index.UpsertDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        var checkpoint = await _index.GetCheckpointAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var localCount = await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false);
        var fullScan = checkpoint is null || device.MediaCount < localCount;
        var result = await ScanAsync(source, device.Id, checkpoint, fullScan, cancellationToken)
            .ConfigureAwait(false);

        if (!result.WasFullScan &&
            await _index.CountAsync(device.Id, cancellationToken).ConfigureAwait(false) > device.MediaCount)
        {
            var reconciliation = await ScanAsync(
                source,
                device.Id,
                checkpoint: null,
                fullScan: true,
                cancellationToken).ConfigureAwait(false);
            result = new ScanResult(
                result.PagesFetched + reconciliation.PagesFetched,
                result.ItemsReceived + reconciliation.ItemsReceived,
                reconciliation.ItemsRemoved,
                WasFullScan: true);
        }

        return new MediaSyncResult(
            device,
            result.PagesFetched,
            result.ItemsReceived,
            result.ItemsRemoved,
            result.WasFullScan);
    }

    private async Task<ScanResult> ScanAsync(
        IReadOnlyMediaSource source,
        string deviceId,
        SyncCheckpoint? checkpoint,
        bool fullScan,
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
        var removed = await _index.CompleteAsync(
            deviceId,
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
