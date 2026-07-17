using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls.Primitives;
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
    private static readonly ThumbnailSize ViewerPreviewSize = new(1600, 1600);
    private readonly IReadOnlyList<MediaItem> _items;
    private readonly CachingReadOnlyMediaSource? _source;
    private readonly LocalCopyCatalog _localCopies;
    private readonly IMediaThumbnailCache _thumbnailCache;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _videoOpenTimer;
    private readonly VideoPlaybackState _videoState = new();
    private CancellationTokenSource? _loadCancellation;
    private int _index;
    private bool _isUpdatingProgress;
    private bool _isSeeking;
    private bool _isBuffering;
    private LoopbackMediaPlaybackServer? _playbackServer;
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
        _videoOpenTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _videoOpenTimer.Tick += OnVideoOpenTimeout;

        InitializeComponent();
        VideoProgress.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(OnVideoSeekStarted));
        VideoProgress.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnVideoSeekCompleted));
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
        LoadingText.Text = "正在准备高质量预览…";
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
        BitmapSource bitmap;
        if (localCopy is not null)
        {
            bitmap = await Task.Run(
                () => DecodeLocalPhoto(localCopy.LocalPath, ViewerPreviewSize.Width),
                cancellationToken);
        }
        else
        {
            await using var content = await OpenRemoteOrCachedPhotoAsync(
                ViewerPreviewSize,
                cancellationToken);
            bitmap = await Task.Run(
                () => DecodePreview(content, ViewerPreviewSize.Width),
                cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        PhotoImage.Source = bitmap;
        SetPanel(PhotoPanel);
        SetZoomControls(Visibility.Visible);
    }

    private async Task<Stream> OpenRemoteOrCachedPhotoAsync(
        ThumbnailSize size,
        CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            return await OpenCachedThumbnailOrThrowAsync(size, cancellationToken);
        }

        if (_source.IsOffline)
        {
            return await OpenCachedThumbnailOrThrowAsync(size, cancellationToken);
        }

        try
        {
            // MediaStore performs the display-sized decode on Android. The desktop
            // never downloads or expands the source-resolution original for preview.
            return await _source.OpenThumbnailAsync(Current.RemoteId, size, cancellationToken);
        }
        catch (HttpRequestException) when (
            _thumbnailCache.IsThumbnailCached(Current, size) ||
            _thumbnailCache.IsThumbnailCached(Current, OfflinePreviewSize))
        {
            return await OpenCachedThumbnailOrThrowAsync(size, cancellationToken);
        }
    }

    private async Task<Stream> OpenCachedThumbnailOrThrowAsync(
        ThumbnailSize size,
        CancellationToken cancellationToken)
    {
        var cached = await _thumbnailCache.OpenCachedThumbnailAsync(
            Current,
            size,
            cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        cached = await _thumbnailCache.OpenCachedThumbnailAsync(
            Current,
            OfflinePreviewSize,
            cancellationToken);
        return cached ?? throw new IOException("设备离线，且此照片只有元数据。");
    }

    private void LoadVideo(LocalCopy? localCopy)
    {
        Uri source;
        if (localCopy is not null)
        {
            source = new Uri(Path.GetFullPath(localCopy.LocalPath));
        }
        else if (_source is not null && !_source.IsOffline)
        {
            _playbackServer = new LoopbackMediaPlaybackServer(_source, Current);
            source = _playbackServer.SourceUri;
        }
        else
        {
            ShowError("设备离线，且此视频没有本地副本。");
            return;
        }

        VideoPlayer.Source = source;
        VideoProgress.Maximum = Math.Max(Current.DurationMilliseconds ?? 0, 1);
        VideoProgress.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        PlayPauseButton.Content = "播放";
        VideoStatusText.Text = "正在加载视频…";
        _videoState.BeginLoading();
        SetPanel(VideoPanel);
        SetZoomControls(Visibility.Collapsed);
        _videoOpenTimer.Start();
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

        var cached =
            _thumbnailCache.IsThumbnailCached(Current, ViewerPreviewSize) ||
            _thumbnailCache.IsThumbnailCached(Current, OfflinePreviewSize);
        AvailabilityText.Text = cached ? "有缓存（缩略图）" : "仅元数据";
        if (_source is not null && !_source.IsOffline)
        {
            AvailabilityText.Text += " · 设备在线";
        }
    }

    private static BitmapSource DecodeLocalPhoto(string path, int decodePixelWidth)
    {
        ushort orientation;
        using (var metadataStream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.SequentialScan))
        {
            var frame = BitmapFrame.Create(
                metadataStream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
        }

        using var imageStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        return ApplyOrientation(DecodePreview(imageStream, decodePixelWidth), orientation);
    }

    private static BitmapImage DecodePreview(Stream stream, int decodePixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource ApplyOrientation(BitmapSource frame, ushort orientation)
    {
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

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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

    private void OnZoomActualClick(object sender, RoutedEventArgs e) =>
        PhotoScale.ScaleX = PhotoScale.ScaleY = 1;

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ChangeZoom(0.2);

    private void ChangeZoom(double change)
    {
        var zoom = Math.Clamp(PhotoScale.ScaleX + change, 0.2, 5);
        PhotoScale.ScaleX = PhotoScale.ScaleY = zoom;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayback();

    private void OnVideoVolumeChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) => VideoPlayer.Volume = e.NewValue;

    private void TogglePlayback()
    {
        if (!_videoState.CanControl)
        {
            VideoStatusText.Text = "视频仍在加载，请稍候…";
            return;
        }

        if (_videoState.Status == VideoPlaybackStatus.Playing)
        {
            VideoPlayer.Pause();
            _playbackTimer.Stop();
            VideoStatusText.Text = "已暂停";
            _videoState.Pause();
        }
        else
        {
            if (_videoState.Status == VideoPlaybackStatus.Ended)
            {
                VideoPlayer.Position = TimeSpan.Zero;
                VideoProgress.Value = 0;
            }

            VideoPlayer.Play();
            _playbackTimer.Start();
            VideoStatusText.Text = "正在播放";
            _videoState.Play();
        }

        PlayPauseButton.Content =
            _videoState.Status == VideoPlaybackStatus.Playing ? "暂停" : "播放";
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (_videoState.Status != VideoPlaybackStatus.Loading)
        {
            return;
        }

        _videoOpenTimer.Stop();
        _videoState.MarkReady();
        PlayPauseButton.IsEnabled = true;
        VideoProgress.IsEnabled = true;
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoProgress.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalMilliseconds;
        }

        VideoStatusText.Text = "已就绪 · 点击播放";
        UpdateVideoTime();
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        if (_videoState.Status != VideoPlaybackStatus.Playing)
        {
            return;
        }

        VideoPlayer.Stop();
        _videoState.MarkEnded();
        _playbackTimer.Stop();
        PlayPauseButton.Content = "播放";
        VideoStatusText.Text = "播放结束 · 可重新播放";
        UpdateVideoTime();
    }

    private void OnVideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_videoState.Status == VideoPlaybackStatus.Idle)
        {
            return;
        }

        _videoOpenTimer.Stop();
        _videoState.MarkFailed();
        var detail = string.IsNullOrWhiteSpace(e.ErrorException?.Message)
            ? "播放器无法打开该视频格式或视频流。"
            : e.ErrorException.Message;
        ShowError($"视频播放失败：{detail}");
    }

    private void OnVideoBufferingStarted(object sender, RoutedEventArgs e)
    {
        _isBuffering = true;
        if (!_isSeeking && _videoState.Status == VideoPlaybackStatus.Playing)
        {
            VideoStatusText.Text = "正在缓冲…";
        }
    }

    private void OnVideoBufferingEnded(object sender, RoutedEventArgs e)
    {
        _isBuffering = false;
        if (!_isSeeking && _videoState.Status == VideoPlaybackStatus.Playing)
        {
            VideoStatusText.Text = "正在播放";
        }
    }

    private void OnVideoOpenTimeout(object? sender, EventArgs e)
    {
        _videoOpenTimer.Stop();
        if (_videoState.Status == VideoPlaybackStatus.Loading &&
            VideoPanel.Visibility == Visibility.Visible)
        {
            ShowError("视频加载超时。请检查设备连接后重试。");
        }
    }

    private void OnVideoProgressChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingProgress || !VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        if (_isSeeking)
        {
            UpdateVideoTime(TimeSpan.FromMilliseconds(e.NewValue));
            return;
        }

        VideoPlayer.Position = TimeSpan.FromMilliseconds(e.NewValue);
        VideoStatusText.Text = _videoState.Status == VideoPlaybackStatus.Playing
            ? "正在定位…"
            : "已定位";
        UpdateVideoTime();
    }

    private void OnVideoSeekStarted(object sender, DragStartedEventArgs e)
    {
        if (!_videoState.CanControl)
        {
            return;
        }

        _isSeeking = true;
        _videoState.BeginSeek();
        if (_videoState.ResumeAfterSeek)
        {
            VideoPlayer.Pause();
        }

        _playbackTimer.Stop();
        PlayPauseButton.IsEnabled = false;
        VideoStatusText.Text = "拖动选择目标时间…";
    }

    private void OnVideoSeekCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isSeeking || _videoState.Status != VideoPlaybackStatus.Seeking)
        {
            return;
        }

        var resume = _videoState.ResumeAfterSeek;
        var target = e.Canceled
            ? VideoPlayer.Position
            : TimeSpan.FromMilliseconds(VideoProgress.Value);
        _isSeeking = false;
        VideoPlayer.Position = target;
        _videoState.CompleteSeek();

        _isUpdatingProgress = true;
        VideoProgress.Value = target.TotalMilliseconds;
        _isUpdatingProgress = false;
        UpdateVideoTime(target);
        PlayPauseButton.IsEnabled = true;
        if (resume)
        {
            VideoPlayer.Play();
            _playbackTimer.Start();
            VideoStatusText.Text = "正在播放";
        }
        else
        {
            VideoStatusText.Text = "已定位";
        }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (_isSeeking || !VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        _isUpdatingProgress = true;
        VideoProgress.Value = VideoPlayer.Position.TotalMilliseconds;
        _isUpdatingProgress = false;
        UpdateVideoTime();
        if (!_isBuffering && _videoState.Status == VideoPlaybackStatus.Playing)
        {
            VideoStatusText.Text = "正在播放";
        }
    }

    private void UpdateVideoTime(TimeSpan? previewPosition = null)
    {
        var total = VideoPlayer.NaturalDuration.HasTimeSpan
            ? VideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.FromMilliseconds(Current.DurationMilliseconds ?? 0);
        var position = previewPosition ?? VideoPlayer.Position;
        VideoTimeText.Text = $"{FormatDuration(position)} / {FormatDuration(total)}";
    }

    private void StopVideo()
    {
        _playbackTimer.Stop();
        _videoOpenTimer.Stop();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        _playbackServer?.Dispose();
        _playbackServer = null;
        _videoState.Reset();
        _isSeeking = false;
        _isBuffering = false;
        PlayPauseButton.Content = "播放";
        PlayPauseButton.IsEnabled = false;
        VideoProgress.IsEnabled = false;
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
        ZoomActualButton.Visibility = visibility;
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
