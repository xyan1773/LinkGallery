using System.Text.Json;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;

namespace LinkGallery.Infrastructure.Media;

public sealed class LocalCopyCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _catalogPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalCopyCatalog(string catalogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        _catalogPath = Path.GetFullPath(catalogPath);
    }

    public async Task<LocalCopy?> FindAsync(
        MediaItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var copies = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var copy = copies.FirstOrDefault(candidate =>
                candidate.DeviceId == item.DeviceId &&
                candidate.RemoteId == item.RemoteId &&
                candidate.FileSize == item.FileSize);
            return copy is not null && File.Exists(copy.LocalPath) ? copy : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RegisterAsync(
        LocalCopy copy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var copies = await LoadAsync(cancellationToken).ConfigureAwait(false);
            copies.RemoveAll(candidate =>
                candidate.DeviceId == copy.DeviceId &&
                candidate.RemoteId == copy.RemoteId);
            copies.Add(copy);

            Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
            var temporaryPath = $"{_catalogPath}.{Guid.NewGuid():N}.tmp";
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
                        copies,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _catalogPath, true);
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
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<List<LocalCopy>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                _catalogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<List<LocalCopy>>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
