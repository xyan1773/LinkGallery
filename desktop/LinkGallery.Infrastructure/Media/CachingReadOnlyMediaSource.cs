using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Media;

public sealed class CachingReadOnlyMediaSource : IReadOnlyMediaSource, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyMediaSource _inner;
    private readonly ThumbnailDiskCache _thumbnailCache;
    private readonly string _timelinePath;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _modifiedAt = new(StringComparer.Ordinal);
    private TimelineSnapshot _snapshot = new();

    public CachingReadOnlyMediaSource(
        IReadOnlyMediaSource inner,
        string cacheRoot,
        string cacheIdentity,
        long thumbnailCacheBytes = 512L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheIdentity);
        _inner = inner;
        var root = Path.GetFullPath(cacheRoot);
        var identityHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)));
        _timelinePath = Path.Combine(root, "timelines", $"{identityHash}.json");
        _thumbnailCache = new ThumbnailDiskCache(
            Path.Combine(root, "thumbnails"),
            thumbnailCacheBytes);
        LoadSnapshot();
    }

    public bool IsOffline { get; private set; }

    public async Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var device = await _inner.GetDeviceInfoAsync(cancellationToken).ConfigureAwait(false);
            device.IsOnline = true;
            IsOffline = false;
            _snapshot.Device = device;
            await SaveSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return device;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            var cached = _snapshot.Device;
            if (cached is null)
            {
                throw;
            }

            cached.IsOnline = false;
            IsOffline = true;
            return cached;
        }
    }

    public async Task<MediaPage> GetMediaPageAsync(
        MediaQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var key = PageKey(query);
        try
        {
            var page = await _inner.GetMediaPageAsync(query, cancellationToken).ConfigureAwait(false);
            IsOffline = false;
            Remember(page.Items);
            _snapshot.Pages[key] = new CachedPage([.. page.Items], page.NextCursor);
            await SaveSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return page;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            if (!_snapshot.Pages.TryGetValue(key, out var cached))
            {
                throw;
            }

            IsOffline = true;
            Remember(cached.Items);
            return new MediaPage(cached.Items, cached.NextCursor);
        }
    }

    public Task<Stream> OpenThumbnailAsync(
        string remoteId,
        ThumbnailSize size,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        var deviceId = _snapshot.Device?.Id ?? "unknown-device";
        var modifiedAt = _modifiedAt.GetValueOrDefault(remoteId).UtcTicks;
        return _thumbnailCache.GetOrCreateAsync(
            new ThumbnailCacheKey(deviceId, remoteId, modifiedAt, size.Width, size.Height),
            token => _inner.OpenThumbnailAsync(remoteId, size, token),
            cancellationToken);
    }

    public Task<Stream> OpenOriginalAsync(
        string remoteId,
        long offset,
        CancellationToken cancellationToken) =>
        _inner.OpenOriginalAsync(remoteId, offset, cancellationToken);

    public Task ClearThumbnailCacheAsync(CancellationToken cancellationToken = default) =>
        _thumbnailCache.ClearAsync(cancellationToken);

    public void Dispose()
    {
        _cacheLock.Dispose();
        _thumbnailCache.Dispose();
    }

    private void LoadSnapshot()
    {
        if (!File.Exists(_timelinePath))
        {
            return;
        }

        try
        {
            _snapshot = JsonSerializer.Deserialize<TimelineSnapshot>(
                File.ReadAllText(_timelinePath),
                JsonOptions) ?? new TimelineSnapshot();
            foreach (var page in _snapshot.Pages.Values)
            {
                Remember(page.Items);
            }
        }
        catch (JsonException)
        {
            _snapshot = new TimelineSnapshot();
        }
    }

    private async Task SaveSnapshotAsync(CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_timelinePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_timelinePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        _snapshot,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _timelinePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void Remember(IEnumerable<MediaItem> items)
    {
        foreach (var item in items)
        {
            _modifiedAt[item.RemoteId] = item.ModifiedAt;
        }
    }

    private static string PageKey(MediaQuery query)
    {
        var types = query.Types is null
            ? ""
            : string.Join(',', query.Types.OrderBy(static type => type));
        return $"{query.Cursor ?? "<first>"}|{query.Limit}|{types}";
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is MediaSourceTimeoutException ||
        exception is HttpRequestException and not MediaSourceHttpException;

    public sealed class TimelineSnapshot
    {
        public Device? Device { get; set; }

        public Dictionary<string, CachedPage> Pages { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed record CachedPage(List<MediaItem> Items, string? NextCursor);
}
