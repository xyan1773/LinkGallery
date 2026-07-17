using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Media;

public sealed class LoopbackMediaPlaybackServer : IDisposable
{
    private const int MaximumHeaderCharacters = 32 * 1024;
    private readonly IReadOnlyMediaSource _source;
    private readonly MediaItem _item;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly string _sessionToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private int _nextClientId;
    private bool _disposed;

    public LoopbackMediaPlaybackServer(IReadOnlyMediaSource source, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfNegative(item.FileSize);

        _source = source;
        _item = item;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        SourceUri = new Uri(
            $"http://127.0.0.1:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}/{_sessionToken}/{Uri.EscapeDataString(item.FileName)}");
        _ = AcceptClientsAsync(_shutdown.Token);
    }

    public Uri SourceUri { get; }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var clientId = Interlocked.Increment(ref _nextClientId);
                _clients[clientId] = client;
                _ = HandleAndRemoveClientAsync(clientId, client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAndRemoveClientAsync(
        int clientId,
        TcpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // MediaElement can abandon a speculative range request when it seeks.
        }
        catch (SocketException)
        {
            // Closing the viewer can reset an in-flight loopback connection.
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            client.Dispose();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteErrorAsync(stream, 405, "Method Not Allowed", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate("http://localhost" + request.Target, UriKind.Absolute, out var requestUri) ||
            !requestUri.AbsolutePath.StartsWith('/' + _sessionToken + '/', StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        request.Headers.TryGetValue("Range", out var rangeHeader);
        if (!TryParseRange(rangeHeader, _item.FileSize, out var range))
        {
            await WriteRangeNotSatisfiableAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "HEAD" || range.Length == 0)
        {
            await WriteSuccessHeadersAsync(stream, range, cancellationToken).ConfigureAwait(false);
            return;
        }

        Stream remote;
        try
        {
            remote = await _source.OpenOriginalAsync(
                _item.RemoteId,
                range.Start,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(stream, 502, "Bad Gateway", cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (remote.ConfigureAwait(false))
        {
            await WriteSuccessHeadersAsync(stream, range, cancellationToken).ConfigureAwait(false);
            await CopyExactlyAsync(remote, stream, range.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteSuccessHeadersAsync(
        Stream stream,
        ByteRange range,
        CancellationToken cancellationToken)
    {
        var status = range.IsPartial ? "206 Partial Content" : "200 OK";
        var contentRange = range.IsPartial
            ? $"Content-Range: bytes {range.Start.ToString(CultureInfo.InvariantCulture)}-{range.End.ToString(CultureInfo.InvariantCulture)}/{_item.FileSize.ToString(CultureInfo.InvariantCulture)}\r\n"
            : string.Empty;
        var headers =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {SafeContentType()}\r\n" +
            $"Content-Length: {range.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Accept-Ranges: bytes\r\n" +
            contentRange +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRangeNotSatisfiableAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headers =
            "HTTP/1.1 416 Range Not Satisfiable\r\n" +
            $"Content-Range: bytes */{_item.FileSize.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        Stream stream,
        int statusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(reason);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode.ToString(CultureInfo.InvariantCulture)} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private string SafeContentType()
    {
        var contentType = _item.MimeType;
        return contentType is not null &&
            contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.AsSpan().ContainsAny('\r', '\n')
                ? contentType
                : "video/mp4";
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The remote media stream ended before the requested range.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<PlaybackRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var totalCharacters = requestLine.Length;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                break;
            }

            totalCharacters += line.Length;
            if (totalCharacters > MaximumHeaderCharacters)
            {
                return null;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return new PlaybackRequest(parts[0].ToUpperInvariant(), parts[1], headers);
    }

    private static bool TryParseRange(string? header, long totalLength, out ByteRange range)
    {
        if (header is null)
        {
            range = totalLength == 0
                ? new ByteRange(0, -1, IsPartial: false)
                : new ByteRange(0, totalLength - 1, IsPartial: false);
            return true;
        }

        range = default;
        if (totalLength == 0 ||
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) ||
            header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var values = header[6..].Split('-', 2);
        if (values.Length != 2)
        {
            return false;
        }

        long start;
        long end;
        if (values[0].Length == 0)
        {
            if (!long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffixLength) ||
                suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(0, totalLength - suffixLength);
            end = totalLength - 1;
        }
        else
        {
            if (!long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
                start < 0 ||
                start >= totalLength)
            {
                return false;
            }

            if (values[1].Length == 0)
            {
                end = totalLength - 1;
            }
            else if (!long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) ||
                     end < start)
            {
                return false;
            }

            end = Math.Min(end, totalLength - 1);
        }

        range = new ByteRange(start, end, IsPartial: true);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record PlaybackRequest(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers);

    private readonly record struct ByteRange(long Start, long End, bool IsPartial)
    {
        public long Length => End >= Start ? End - Start + 1 : 0;
    }
}
