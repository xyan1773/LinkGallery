using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Desktop;

public partial class MediaDetailWindow : Window, IDisposable
{
    private static readonly ThumbnailSize OfflinePreviewSize = new(320, 240);
    private readonly IReadOnlyList<MediaItem> _items;
    private readonly CachingReadOnlyMediaSource? _source;
    private readonly LocalCopyCatalog _localCopies;
    private readonly IMediaThumbnailCache _thumbnailCache;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _loadCancellation;
    private int _index;
    private bool _isPlaying;
    private bool _isUpdatingProgress;
    private bool _disposed;

    public MediaDetailWindow(
        IReadOnlyList<MediaItem> items,
        int selectedIndex,
        CachingReadOnlyMediaSource? source,
        LocalCopyCatalog localCopies,
        IMediaThumbnailCache thumbnailCache)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(localCopies);
        ArgumentNullException.ThrowIfNull(thumbnailCache);
        if (items.Count == 0)
        {
            throw new ArgumentException("详情窗口至少需要一项媒体。", nameof(items));
        }

        _items = items;
        _index = Math.Clamp(selectedIndex, 0, items.Count - 1);
        _source = source;
        _localCopies = localCopies;
        _thumbnailCache = thumbnailCache;
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;

        InitializeComponent();
        Loaded += async (_, _) => await LoadCurrentAsync();
    }

    private MediaItem Current => _items[_index];

    private async Task LoadCurrentAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        StopVideo();
        PhotoImage.Source = null;
        PhotoScale.ScaleX = PhotoScale.ScaleY = 1;
        SetPanel(LoadingPanel);
        LoadingText.Text = "正在加载…";
        UpdateMetadata();

        try
        {
            var localCopy = await _localCopies.FindAsync(Current, cancellationToken);
            UpdateAvailability(localCopy);
            if (Current.Type == MediaType.Image)
            {
                await LoadPhotoAsync(localCopy, cancellationToken);
            }
            else
            {
                LoadVideo(localCopy);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowError(ToFriendlyError(exception));
        }
    }

    private async Task LoadPhotoAsync(LocalCopy? localCopy, CancellationToken cancellationToken)
    {
        await using var content = localCopy is not null
            ? new FileStream(
                localCopy.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous)
            : await OpenRemoteOrCachedPhotoAsync(cancellationToken);

        await using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        var bitmap = await Task.Run(() => DecodePhoto(bytes), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        PhotoImage.Source = bitmap;
        SetPanel(PhotoPanel);
        SetZoomControls(Visibility.Visible);
    }

    private async Task<Stream> OpenRemoteOrCachedPhotoAsync(CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            return await OpenCachedThumbnailOrThrowAsync(cancellationToken);
        }

        if (_source.IsOffline)
        {
            return await OpenCachedThumbnailOrThrowAsync(cancellationToken);
        }

        try
        {
            return await _source.OpenOriginalAsync(Current.RemoteId, 0, cancellationToken);
        }
        catch (HttpRequestException) when (
            _thumbnailCache.IsThumbnailCached(Current, OfflinePreviewSize))
        {
            return await OpenCachedThumbnailOrThrowAsync(cancellationToken);
        }
    }

    private async Task<Stream> OpenCachedThumbnailOrThrowAsync(
        CancellationToken cancellationToken) =>
        await _thumbnailCache.OpenCachedThumbnailAsync(
            Current,
            OfflinePreviewSize,
            cancellationToken) ??
        throw new IOException("设备离线，且此照片只有元数据。");

    private void LoadVideo(LocalCopy? localCopy)
    {
        Uri source;
        if (localCopy is not null)
        {
            source = new Uri(Path.GetFullPath(localCopy.LocalPath));
        }
        else if (_source is not null && !_source.IsOffline)
        {
            source = _source.GetOriginalUri(Current.RemoteId);
        }
        else
        {
            ShowError("设备离线，且此视频没有本地副本。");
            return;
        }

        VideoPlayer.Source = source;
        VideoProgress.Maximum = Math.Max(Current.DurationMilliseconds ?? 0, 1);
        SetPanel(VideoPanel);
        SetZoomControls(Visibility.Collapsed);
        VideoPlayer.Play();
        _isPlaying = true;
        PlayPauseButton.Content = "暂停";
        _playbackTimer.Start();
    }

    private void UpdateMetadata()
    {
        var item = Current;
        Title = $"{item.FileName} - LinkGallery";
        FileNameText.Text = item.FileName;
        PositionText.Text = $"{_index + 1:N0} / {_items.Count:N0}";
        PreviousButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;
        TakenAtText.Text = item.TakenAt.LocalDateTime.ToString(
            "yyyy年M月d日 HH:mm:ss",
            CultureInfo.CurrentCulture);
        DimensionsText.Text = item.Width.HasValue && item.Height.HasValue
            ? $"{item.Width:N0} × {item.Height:N0}"
            : "未知";
        SizeText.Text = FormatSize(item.FileSize);
        SourceText.Text = string.Join(
            " · ",
            new[] { item.SourceDevice, item.SourceApplication, item.AlbumName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .DefaultIfEmpty("手机媒体库"));
        PathText.Text = string.IsNullOrWhiteSpace(item.RelativePath) ? "未知" : item.RelativePath;
        TypeText.Text = item.Type == MediaType.Image
            ? "照片"
            : $"视频 · {FormatDuration(item.DurationMilliseconds)}";
    }

    private void UpdateAvailability(LocalCopy? localCopy)
    {
        if (localCopy is not null)
        {
            AvailabilityText.Text = "已有本地副本";
            return;
        }

        var cached = _thumbnailCache.IsThumbnailCached(Current, OfflinePreviewSize);
        AvailabilityText.Text = cached ? "有缓存（缩略图）" : "仅元数据";
        if (_source is not null && !_source.IsOffline)
        {
            AvailabilityText.Text += " · 设备在线";
        }
    }

    private static BitmapSource DecodePhoto(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
        BitmapSource result = orientation switch
        {
            2 => Transform(frame, new ScaleTransform(-1, 1)),
            3 => Transform(frame, new RotateTransform(180)),
            4 => Transform(frame, new ScaleTransform(1, -1)),
            5 => Transform(frame, Group(new RotateTransform(90), new ScaleTransform(-1, 1))),
            6 => Transform(frame, new RotateTransform(90)),
            7 => Transform(frame, Group(new RotateTransform(270), new ScaleTransform(-1, 1))),
            8 => Transform(frame, new RotateTransform(270)),
            _ => frame,
        };
        result.Freeze();
        return result;
    }

    private static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        try
        {
            return metadata?.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation
                ? orientation
                : (ushort)1;
        }
        catch (NotSupportedException)
        {
            return 1;
        }
    }

    private static TransformedBitmap Transform(BitmapSource source, Transform transform)
    {
        transform.Freeze();
        var bitmap = new TransformedBitmap(source, transform);
        bitmap.Freeze();
        return bitmap;
    }

    private static TransformGroup Group(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var transform in transforms)
        {
            group.Children.Add(transform);
        }

        return group;
    }

    private void Move(int change)
    {
        var next = _index + change;
        if (next < 0 || next >= _items.Count)
        {
            return;
        }

        _index = next;
        _ = LoadCurrentAsync();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Move(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Move(1);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            Move(-1);
        }
        else if (e.Key == Key.Right)
        {
            Move(1);
        }
        else if (e.Key == Key.Space && Current.Type == MediaType.Video)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnPhotoMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeZoom(e.Delta > 0 ? 0.2 : -0.2);
        e.Handled = true;
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ChangeZoom(-0.2);

    private void OnZoomResetClick(object sender, RoutedEventArgs e) =>
        PhotoScale.ScaleX = PhotoScale.ScaleY = 1;

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ChangeZoom(0.2);

    private void ChangeZoom(double change)
    {
        var zoom = Math.Clamp(PhotoScale.ScaleX + change, 0.2, 5);
        PhotoScale.ScaleX = PhotoScale.ScaleY = zoom;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_isPlaying)
        {
            VideoPlayer.Pause();
            _playbackTimer.Stop();
        }
        else
        {
            VideoPlayer.Play();
            _playbackTimer.Start();
        }

        _isPlaying = !_isPlaying;
        PlayPauseButton.Content = _isPlaying ? "暂停" : "播放";
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoProgress.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalMilliseconds;
        }

        UpdateVideoTime();
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Stop();
        _isPlaying = false;
        _playbackTimer.Stop();
        PlayPauseButton.Content = "播放";
        UpdateVideoTime();
    }

    private void OnVideoFailed(object sender, ExceptionRoutedEventArgs e) =>
        ShowError($"视频播放失败：{e.ErrorException?.Message ?? "不支持的格式或内容不可用"}");

    private void OnVideoProgressChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingProgress || !VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        VideoPlayer.Position = TimeSpan.FromMilliseconds(e.NewValue);
        UpdateVideoTime();
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (!VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        _isUpdatingProgress = true;
        VideoProgress.Value = VideoPlayer.Position.TotalMilliseconds;
        _isUpdatingProgress = false;
        UpdateVideoTime();
    }

    private void UpdateVideoTime()
    {
        var total = VideoPlayer.NaturalDuration.HasTimeSpan
            ? VideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.FromMilliseconds(Current.DurationMilliseconds ?? 0);
        VideoTimeText.Text = $"{FormatDuration(VideoPlayer.Position)} / {FormatDuration(total)}";
    }

    private void StopVideo()
    {
        _playbackTimer.Stop();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        _isPlaying = false;
        PlayPauseButton.Content = "播放";
        VideoProgress.Value = 0;
    }

    private void ShowError(string message)
    {
        StopVideo();
        ErrorText.Text = message;
        SetPanel(ErrorPanel);
        SetZoomControls(Visibility.Collapsed);
    }

    private void SetPanel(UIElement panel)
    {
        PhotoPanel.Visibility = panel == PhotoPanel ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = panel == VideoPanel ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = panel == LoadingPanel ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = panel == ErrorPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetZoomControls(Visibility visibility)
    {
        ZoomOutButton.Visibility = visibility;
        ZoomResetButton.Visibility = visibility;
        ZoomInButton.Visibility = visibility;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        StopVideo();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string ToFriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => "无法从手机读取内容，请检查连接后重试。",
        IOException => exception.Message,
        NotSupportedException => exception.Message,
        _ => $"加载失败：{exception.Message}",
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatDuration(long? milliseconds) =>
        milliseconds.HasValue
            ? FormatDuration(TimeSpan.FromMilliseconds(milliseconds.Value))
            : "时长未知";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
