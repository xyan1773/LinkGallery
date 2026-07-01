using System.IO;
using LinkGallery.Application.Media;
using LinkGallery.Application.Transfers;

namespace LinkGallery.Desktop;

internal sealed class CurrentTransferMediaSourceResolver : ITransferMediaSourceResolver
{
    private readonly object _sync = new();
    private string? _deviceId;
    private IReadOnlyMediaSource? _source;

    public void SetSource(string deviceId, IReadOnlyMediaSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(source);
        lock (_sync)
        {
            _deviceId = deviceId;
            _source = source;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _deviceId = null;
            _source = null;
        }
    }

    public ValueTask<IReadOnlyMediaSource> ResolveAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return _source is not null &&
                   string.Equals(_deviceId, deviceId, StringComparison.Ordinal)
                ? ValueTask.FromResult(_source)
                : ValueTask.FromException<IReadOnlyMediaSource>(
                    new IOException("The source device is offline."));
        }
    }
}
