using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Media;

public sealed class HttpReadOnlyMediaSource :
    IReadOnlyMediaSource,
    IMediaPlaybackUriSource,
    IEntityAwareMediaSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseAddress;
    private readonly TimeSpan _requestTimeout;
    private string? _deviceId;

    public HttpReadOnlyMediaSource(HttpClient httpClient, Uri apiBaseAddress, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        if (!apiBaseAddress.IsAbsoluteUri ||
            (apiBaseAddress.Scheme != Uri.UriSchemeHttp && apiBaseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("API 地址必须是绝对 HTTP 或 HTTPS 地址。", nameof(apiBaseAddress));
        }

        _httpClient = httpClient;
        _apiBaseAddress = EnsureTrailingSlash(apiBaseAddress);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
    }

    public static Uri NormalizeApiAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var candidate = address.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new FormatException("请输入有效的手机地址，例如 192.168.1.20:39570。");
        }

        var builder = new UriBuilder(uri);
        if (uri.IsDefaultPort)
        {
            builder.Port = 39570;
        }

        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{path}/"
            : $"{path}/api/v1/";
        return builder.Uri;
    }

    public static bool IsPotentialAndroidEmulatorNatAddress(Uri apiAddress)
    {
        ArgumentNullException.ThrowIfNull(apiAddress);
        return IPAddress.TryParse(apiAddress.Host, out var parsedAddress) &&
            IsAndroidEmulatorNatAddress(parsedAddress);
    }

    public async Task<Device> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var dto = await GetJsonAsync<DeviceDto>("device", cancellationToken).ConfigureAwait(false);
        ValidateDevice(dto);
        _deviceId = dto.Id;
        return new Device
        {
            Id = dto.Id,
            Name = dto.Name,
            Platform = dto.Platform,
            Model = dto.Model,
            BatteryPercent = dto.Battery,
            MediaCount = dto.MediaCount,
            Address = _apiBaseAddress,
            IsOnline = true,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
    }

    public async Task<MediaPage> GetMediaPageAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "每页数量必须在 1 到 200 之间。");
        }

        var parameters = new List<string>
        {
            $"limit={query.Limit.ToString(CultureInfo.InvariantCulture)}",
        };
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            parameters.Add($"cursor={Uri.EscapeDataString(query.Cursor)}");
        }

        if (query.Types is { Count: > 0 })
        {
            parameters.Add($"type={Uri.EscapeDataString(string.Join(',', query.Types.Select(ToApiType)))}");
        }

        var dto = await GetJsonAsync<MediaPageDto>(
            $"media?{string.Join('&', parameters)}",
            cancellationToken).ConfigureAwait(false);
        if (dto.Items is null)
        {
            throw new MediaSourceProtocolException("手机返回的媒体列表缺少 items 字段。");
        }

        var deviceId = _deviceId ?? throw new InvalidOperationException("请先加载设备信息，再加载媒体列表。");
        return new MediaPage(
            dto.Items.Select(item => ToDomain(item, deviceId)).ToArray(),
            dto.NextCursor,
            dto.HasMore,
            dto.Total);
    }

    public Task<Stream> OpenThumbnailAsync(
        string remoteId,
        ThumbnailSize size,
        CancellationToken cancellationToken) =>
        OpenStreamAsync(
            size.Width == size.Height
                ? $"media/{Uri.EscapeDataString(remoteId)}/thumbnail?size={size.Width}"
                : $"media/{Uri.EscapeDataString(remoteId)}/thumbnail?width={size.Width}&height={size.Height}",
            offset: null,
            entityTag: null,
            cancellationToken);

    public Task<Stream> OpenOriginalAsync(string remoteId, long offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return OpenOriginalAsync(remoteId, offset, entityTag: null, cancellationToken);
    }

    public Task<Stream> OpenOriginalAsync(
        string remoteId,
        long offset,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return OpenStreamAsync(
            $"media/{Uri.EscapeDataString(remoteId)}/content",
            offset,
            entityTag,
            cancellationToken);
    }

    public Uri GetOriginalUri(string remoteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        return new Uri(
            _apiBaseAddress,
            $"media/{Uri.EscapeDataString(remoteId)}/content");
    }

    private async Task<T> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            relativePath,
            offset: null,
            entityTag: null,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new MediaSourceProtocolException("手机返回了空响应。");
        }
        catch (JsonException exception)
        {
            throw new MediaSourceProtocolException("手机返回的数据不符合 LinkGallery 协议。", exception);
        }
    }

    private async Task<Stream> OpenStreamAsync(
        string relativePath,
        long? offset,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(relativePath, offset, entityTag, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseOwnedStream(stream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string relativePath,
        long? offset,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBaseAddress, relativePath));
        if (offset.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(offset.Value, null);
        }

        if (entityTag is not null)
        {
            if (!EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag) ||
                parsedEntityTag.IsWeak)
            {
                throw new ArgumentException("A strong ETag is required for If-Range.", nameof(entityTag));
            }

            request.Headers.IfRange = new RangeConditionHeaderValue(parsedEntityTag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaSourceTimeoutException(
                $"连接手机超过 {_requestTimeout.TotalSeconds:0} 秒，已停止等待。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MediaSourceConnectionException(
                ClassifyConnectionFailure(exception),
                "无法连接手机。",
                exception);
        }

        if (response.IsSuccessStatusCode)
        {
            if (offset > 0)
            {
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent ||
                    range?.Unit != "bytes" ||
                    range.From != offset)
                {
                    response.Dispose();
                    throw new MediaSourceProtocolException(
                        "The phone did not honor the requested resume offset.");
                }
            }

            return response;
        }

        var statusCode = response.StatusCode;
        var message = await ReadProblemMessageAsync(response, cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw new MediaSourceHttpException(statusCode, message);
    }

    private static async Task<string> ReadProblemMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(problem?.Message))
            {
                return problem.Message;
            }
        }
        catch (JsonException)
        {
            // Fall back to a stable status-based message.
        }

        return $"手机返回了 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。";
    }

    private static void ValidateDevice(DeviceDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) ||
            string.IsNullOrWhiteSpace(dto.Name) ||
            string.IsNullOrWhiteSpace(dto.Platform) ||
            dto.MediaCount < 0 ||
            dto.Battery is < 0 or > 100)
        {
            throw new MediaSourceProtocolException("手机返回的设备信息不完整或数值无效。");
        }
    }

    private static MediaItem ToDomain(MediaItemDto dto, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.FileName) || dto.FileSize < 0)
        {
            throw new MediaSourceProtocolException("手机返回了无效的媒体元数据。");
        }

        var type = dto.Type switch
        {
            "image" => MediaType.Image,
            "video" => MediaType.Video,
            _ => throw new MediaSourceProtocolException($"未知媒体类型：{dto.Type}。"),
        };
        return new MediaItem
        {
            DeviceId = deviceId,
            RemoteId = dto.Id,
            FileName = dto.FileName,
            Type = type,
            FileSize = dto.FileSize,
            Width = dto.Width,
            Height = dto.Height,
            DurationMilliseconds = dto.DurationMilliseconds,
            TakenAt = dto.TakenAt,
            ModifiedAt = dto.ModifiedAt,
            AlbumName = dto.AlbumName,
            RelativePath = dto.RelativePath,
            ThumbnailUrl = dto.ThumbnailUrl,
            SourceDevice = dto.SourceDevice,
            SourceApplication = dto.SourceApplication,
            IsEditedExport = dto.IsEditedExport,
        };
    }

    private static string ToApiType(MediaType type) => type switch
    {
        MediaType.Image => "image",
        MediaType.Video => "video",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static Uri EnsureTrailingSlash(Uri address) =>
        address.AbsoluteUri.EndsWith('/') ? address : new Uri($"{address.AbsoluteUri}/");

    private static bool IsAndroidEmulatorNatAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 10 && bytes[1] == 0 && bytes[2] == 2;
    }

    private static MediaSourceConnectionFailure ClassifyConnectionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SocketException socketException)
            {
                continue;
            }

            return socketException.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => MediaSourceConnectionFailure.ConnectionRefused,
                SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                    MediaSourceConnectionFailure.NetworkUnreachable,
                _ => MediaSourceConnectionFailure.Unknown,
            };
        }

        return MediaSourceConnectionFailure.Unknown;
    }

    private sealed record DeviceDto(
        string Id,
        string Name,
        string Platform,
        string? Model,
        int? Battery,
        int MediaCount);

    private sealed record MediaPageDto(
        IReadOnlyList<MediaItemDto>? Items,
        string? NextCursor,
        bool HasMore,
        int? Total);

    private sealed record MediaItemDto(
        string Id,
        string FileName,
        string Type,
        long FileSize,
        int? Width,
        int? Height,
        long? DurationMilliseconds,
        DateTimeOffset TakenAt,
        DateTimeOffset ModifiedAt,
        string? AlbumName,
        string? RelativePath,
        string? ThumbnailUrl,
        string? SourceDevice,
        string? SourceApplication,
        bool IsEditedExport);

    private sealed record ProblemDto(string Code, string Message);

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response)
        : Stream, IRemoteMediaStreamMetadata
    {
        public long? TotalLength =>
            response.Content.Headers.ContentRange?.Length ??
            response.Content.Headers.ContentLength;

        public DateTimeOffset? LastModified => response.Content.Headers.LastModified;

        public string? EntityTag => response.Headers.ETag?.ToString();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

public sealed class MediaSourceProtocolException : Exception
{
    public MediaSourceProtocolException(string message)
        : base(message)
    {
    }

    public MediaSourceProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MediaSourceTimeoutException : TimeoutException
{
    public MediaSourceTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum MediaSourceConnectionFailure
{
    Unknown,
    ConnectionRefused,
    NetworkUnreachable,
}

public sealed class MediaSourceConnectionException : HttpRequestException
{
    public MediaSourceConnectionException(
        MediaSourceConnectionFailure failure,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public MediaSourceConnectionFailure Failure { get; }
}

public sealed class MediaSourceHttpException : HttpRequestException
{
    public MediaSourceHttpException(HttpStatusCode statusCode, string message)
        : base(message, inner: null, statusCode)
    {
    }
}
