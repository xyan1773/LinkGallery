using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LinkGallery.Infrastructure.Media;

public sealed record ThumbnailCacheKey(
    string DeviceId,
    string RemoteId,
    long ModifiedAtUtcTicks,
    int Width,
    int Height);

public sealed class ThumbnailDiskCache : IDisposable
{
    private readonly string _directory;
    private readonly long _maximumBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _entryLocks = new();
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);

    public ThumbnailDiskCache(string directory, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _directory = Path.GetFullPath(directory);
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_directory);
    }

    public long MaximumBytes => _maximumBytes;

    public bool Contains(ThumbnailCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return File.Exists(GetPath(key));
    }

    public async Task<Stream> GetOrCreateAsync(
        ThumbnailCacheKey key,
        Func<CancellationToken, Task<Stream>> fetch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetch);
        var path = GetPath(key);
        var entryLock = _entryLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await entryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return OpenRead(path);
            }

            await using var source = await fetch(cancellationToken).ConfigureAwait(false);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, path);
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            await EnforceLimitAsync(cancellationToken).ConfigureAwait(false);
            return OpenRead(path);
        }
        finally
        {
            entryLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _maintenanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.jpg"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(file);
            }
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public void Dispose()
    {
        _maintenanceLock.Dispose();
        foreach (var entryLock in _entryLocks.Values)
        {
            entryLock.Dispose();
        }

        _entryLocks.Clear();
    }

    private async Task EnforceLimitAsync(CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = Directory.EnumerateFiles(_directory, "*.jpg")
                .Select(static path => new FileInfo(path))
                .OrderBy(static file => file.LastAccessTimeUtc)
                .ToArray();
            var total = files.Sum(static file => file.Length);
            foreach (var file in files)
            {
                if (total <= _maximumBytes)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                total -= file.Length;
                file.Delete();
            }
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private string GetPath(ThumbnailCacheKey key)
    {
        var value = $"{key.DeviceId}\n{key.RemoteId}\n{key.ModifiedAtUtcTicks}\n{key.Width}x{key.Height}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return Path.Combine(_directory, $"{hash}.jpg");
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
}
