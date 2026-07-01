using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private const int PageSize = 50;
    private static readonly ThumbnailSize TimelineThumbnailSize = new(320, 240);
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _thumbnailConcurrency = new(6, 6);
    private readonly SqliteMediaIndex _mediaIndex;
    private readonly IncrementalMediaIndexSynchronizer _synchronizer;
    private readonly LocalCopyCatalog _localCopies;
    private readonly ThumbnailCacheReader _thumbnailCache;
    private CancellationTokenSource? _connectionCancellation;
    private CachingReadOnlyMediaSource? _source;
    private string? _activeDeviceId;
    private bool _hasMoreIndexedItems;
    private bool _isLoadingPage;
    private bool _disposed;

    public MainWindow()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkGallery");
        _mediaIndex = new SqliteMediaIndex(Path.Combine(dataDirectory, "media-index.db"));
        _synchronizer = new IncrementalMediaIndexSynchronizer(_mediaIndex);
        _localCopies = new LocalCopyCatalog(Path.Combine(dataDirectory, "local-copies.json"));
        _thumbnailCache = new ThumbnailCacheReader(
            Path.Combine(dataDirectory, "cache", "thumbnails"));
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<MediaRow> TimelineRows { get; } = [];

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAddressHint();
        try
        {
            await LoadIndexedPageAsync(
                reset: true,
                "本地缓存中还没有媒体",
                CancellationToken.None);
            UpdateIndexedStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法读取本地索引：{exception.Message}";
        }
    }

    private void OnAddressTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (AddressHintText is not null)
        {
            UpdateAddressHint();
        }
    }

    private void UpdateAddressHint()
    {
        const string defaultHint =
            "模拟器：先执行 adb forward，再输入 127.0.0.1:39570；不要输入模拟器显示的 10.0.2.x。";
        try
        {
            var address = HttpReadOnlyMediaSource.NormalizeApiAddress(AddressTextBox.Text);
            var couldBeEmulatorNat =
                HttpReadOnlyMediaSource.IsPotentialAndroidEmulatorNatAddress(address);
            AddressHintText.Text = couldBeEmulatorNat
                ? "提示：10.0.2.x 常见于模拟器内部 NAT，模拟器应使用 adb forward 和 127.0.0.1；若这是真实手机的 Wi-Fi 地址，仍可直接连接。"
                : defaultHint;
            AddressHintText.Foreground = couldBeEmulatorNat
                ? System.Windows.Media.Brushes.DarkOrange
                : System.Windows.Media.Brushes.DimGray;
        }
        catch (FormatException)
        {
            AddressHintText.Text = defaultHint;
            AddressHintText.Foreground = System.Windows.Media.Brushes.DimGray;
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        Disconnect(clearTimeline: true);
        SetLoading(true, "正在连接手机并同步媒体索引…");
        _connectionCancellation = new CancellationTokenSource();
        var cancellationToken = _connectionCancellation.Token;

        try
        {
            var apiAddress = HttpReadOnlyMediaSource.NormalizeApiAddress(AddressTextBox.Text);
            var httpSource = new HttpReadOnlyMediaSource(_httpClient, apiAddress);
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinkGallery",
                "cache");
            _source = new CachingReadOnlyMediaSource(
                httpSource,
                cacheRoot,
                apiAddress.AbsoluteUri);

            var sync = await _synchronizer.SynchronizeAsync(_source, cancellationToken);
            var device = sync.Device;
            _activeDeviceId = device.Id;

            DeviceNameText.Text = device.Name;
            DeviceModelText.Text = string.IsNullOrWhiteSpace(device.Model)
                ? device.Platform
                : $"{device.Model} · {device.Platform}";
            BatteryText.Text = device.BatteryPercent.HasValue
                ? $"电量 {device.BatteryPercent}%"
                : "电量未知";
            MediaCountText.Text = $"共 {device.MediaCount:N0} 项媒体";
            DevicePanel.Visibility = Visibility.Visible;
            DisconnectButton.IsEnabled = true;

            await LoadIndexedPageAsync(
                reset: true,
                "手机中没有可显示的照片或视频",
                cancellationToken);
            var syncMode = sync.WasFullScan ? "完整索引" : "增量更新";
            StatusText.Text =
                $"已连接 · {syncMode} {sync.ItemsReceived:N0} 项（{sync.PagesFetched:N0} 页）" +
                $" · 已显示 {TimelineRows.Count:N0} 项";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "连接已取消";
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
            await LoadIndexedPageAsync(
                reset: true,
                "手机当前不可用，本地缓存中还没有媒体",
                CancellationToken.None);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async Task LoadIndexedPageAsync(
        bool reset,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        if (_isLoadingPage || (!reset && !_hasMoreIndexedItems))
        {
            return;
        }

        _isLoadingPage = true;
        try
        {
            if (reset)
            {
                TimelineRows.Clear();
                _hasMoreIndexedItems = true;
            }

            var items = await _mediaIndex.SearchAsync(
                _activeDeviceId,
                SearchTextBox.Text,
                PageSize,
                TimelineRows.Count,
                cancellationToken);
            AppendTimelineItems(items);
            _hasMoreIndexedItems = items.Count == PageSize;
            TimelineList.Visibility = TimelineRows.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmptyText.Text = emptyMessage;
            EmptyText.Visibility = TimelineRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _isLoadingPage = false;
        }
    }

    private void AppendTimelineItems(IReadOnlyList<MediaItem> items)
    {
        var previousDate = TimelineRows.LastOrDefault()?.Item.TakenAt.LocalDateTime.Date;
        foreach (var item in items)
        {
            var date = item.TakenAt.LocalDateTime.Date;
            var dateHeader = previousDate != date
                ? date.ToString("yyyy年M月d日 dddd", CultureInfo.CurrentCulture)
                : null;
            TimelineRows.Add(new MediaRow(item, dateHeader));
            previousDate = date;
        }
    }

    private async void OnTimelineItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: MediaRow row } ||
            _source is null ||
            row.Thumbnail is not null ||
            row.IsThumbnailLoading)
        {
            return;
        }

        row.IsThumbnailLoading = true;
        var cancellationToken = _connectionCancellation?.Token ?? CancellationToken.None;
        try
        {
            await _thumbnailConcurrency.WaitAsync(cancellationToken);
            try
            {
                await using var stream = await _source.OpenThumbnailAsync(
                    row.Item.RemoteId,
                    TimelineThumbnailSize,
                    cancellationToken);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = TimelineThumbnailSize.Width;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                row.Thumbnail = image;
            }
            finally
            {
                _thumbnailConcurrency.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            ThumbnailLoadFailurePolicy.KeepsPlaceholder(exception))
        {
            // Timeouts, connection failures, and malformed thumbnails keep the
            // placeholder without escaping into the WPF Dispatcher.
        }
        finally
        {
            row.IsThumbnailLoading = false;
        }
    }

    private void OnTimelineDoubleClick(object sender, MouseButtonEventArgs e) =>
        OpenSelectedMedia();

    private void OnTimelineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        OpenSelectedMedia();
        e.Handled = true;
    }

    private void OpenSelectedMedia()
    {
        if (TimelineList.SelectedItem is not MediaRow selected)
        {
            return;
        }

        var items = TimelineRows.Select(static row => row.Item).ToArray();
        var selectedIndex = Array.FindIndex(
            items,
            item => item.DeviceId == selected.Item.DeviceId &&
                item.RemoteId == selected.Item.RemoteId);
        var detail = new MediaDetailWindow(
            items,
            selectedIndex,
            _source,
            _localCopies,
            _thumbnailCache)
        {
            Owner = this,
        };
        detail.Show();
    }

    private async void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 ||
            e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 600 ||
            !_hasMoreIndexedItems)
        {
            return;
        }

        try
        {
            var cancellationToken = _connectionCancellation?.Token ?? CancellationToken.None;
            await LoadIndexedPageAsync(
                reset: false,
                "本地索引中没有可显示的媒体",
                cancellationToken);
            UpdateIndexedStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法继续加载：{exception.Message}";
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadIndexedPageAsync(
                reset: true,
                "本地索引中没有匹配的媒体",
                CancellationToken.None);
            UpdateIndexedStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法读取本地索引：{exception.Message}";
        }
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            StatusText.Text = "连接设备后可清理缩略图缓存";
            return;
        }

        try
        {
            await _source.ClearThumbnailCacheAsync();
            foreach (var row in TimelineRows)
            {
                row.Thumbnail = null;
            }

            TimelineList.Items.Refresh();
            StatusText.Text = "缩略图缓存已清理";
        }
        catch (IOException exception)
        {
            StatusText.Text = $"缓存清理失败：{exception.Message}";
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        Disconnect(clearTimeline: true);
        try
        {
            await LoadIndexedPageAsync(
                reset: true,
                "本地缓存中还没有媒体",
                CancellationToken.None);
            UpdateIndexedStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法读取本地索引：{exception.Message}";
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect(clearTimeline: true);
        _thumbnailConcurrency.Dispose();
        _httpClient.Dispose();
        _localCopies.Dispose();
        _mediaIndex.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Disconnect(bool clearTimeline)
    {
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _source?.Dispose();
        _source = null;
        _activeDeviceId = null;
        _hasMoreIndexedItems = false;
        _isLoadingPage = false;
        DevicePanel.Visibility = Visibility.Collapsed;
        TimelineList.Visibility = Visibility.Collapsed;
        if (clearTimeline)
        {
            TimelineRows.Clear();
        }

        EmptyText.Text = "输入手机地址开始连接";
        EmptyText.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = false;
        SetLoading(false);
    }

    private void SetLoading(bool isLoading, string? status = null)
    {
        LoadingProgress.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = !isLoading;
        AddressTextBox.IsEnabled = !isLoading;
        DisconnectButton.IsEnabled = isLoading || DevicePanel.Visibility == Visibility.Visible;
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    private void UpdateIndexedStatus()
    {
        var mode = _source is null
            ? "离线缓存"
            : _source.IsOffline ? "离线缓存" : "在线";
        var suffix = _hasMoreIndexedItems ? "" : " · 已全部加载";
        StatusText.Text = $"{mode} · 已显示 {TimelineRows.Count:N0} 项{suffix}";
    }

    private void ShowConnectionError(Exception exception)
    {
        DisconnectButton.IsEnabled = false;
        StatusText.Text = exception switch
        {
            FormatException => exception.Message,
            MediaSourceTimeoutException =>
                "连接超时。请确认手机在线、地址正确，且两台设备在同一 Wi-Fi。",
            MediaSourceConnectionException
                { Failure: MediaSourceConnectionFailure.ConnectionRefused } =>
                "连接被拒绝。地址可以到达，但手机服务未监听；请保持 Android 页面在前台后重试。",
            MediaSourceConnectionException
                { Failure: MediaSourceConnectionFailure.NetworkUnreachable } =>
                "网络不可达。真机请检查同一 Wi-Fi、Windows 防火墙和 Wi-Fi AP 客户端隔离；模拟器请使用 ADB forward 和 127.0.0.1。",
            MediaSourceConnectionException =>
                "无法连接手机。真机请检查 IP、端口和 Wi-Fi；模拟器请使用 ADB forward 和 127.0.0.1。",
            MediaSourceProtocolException => $"协议错误：{exception.Message}",
            MediaSourceHttpException { StatusCode: HttpStatusCode.Forbidden } =>
                "手机未授予照片和视频读取权限，请在手机端授权后重试。",
            MediaSourceHttpException { StatusCode: HttpStatusCode.BadRequest } =>
                $"手机拒绝了请求：{exception.Message}",
            MediaSourceHttpException => $"手机返回错误：{exception.Message}",
            HttpRequestException =>
                "无法连接手机。请检查 IP、端口、Wi-Fi 和手机服务状态；仍可浏览本地索引。",
            _ => $"连接失败：{exception.Message}",
        };
    }

    public sealed class MediaRow : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;

        public MediaRow(MediaItem item, string? dateHeader)
        {
            Item = item;
            DateHeader = dateHeader;
            TypeLabel = item.Type == MediaType.Image ? "图片" : "视频";
            FileName = item.FileName;
            Details = FormatDetails(item);
            TakenAt = item.TakenAt.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
            AlbumName = item.AlbumName ?? "未分类";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MediaItem Item { get; }

        public string? DateHeader { get; }

        public string TypeLabel { get; }

        public string FileName { get; }

        public string Details { get; }

        public string TakenAt { get; }

        public string AlbumName { get; }

        public bool IsThumbnailLoading { get; set; }

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value))
                {
                    return;
                }

                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        private static string FormatDetails(MediaItem item)
        {
            if (item.Type == MediaType.Video && item.DurationMilliseconds.HasValue)
            {
                return TimeSpan.FromMilliseconds(item.DurationMilliseconds.Value)
                    .ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            }

            return item.Width.HasValue && item.Height.HasValue
                ? $"{item.Width} × {item.Height}"
                : FormatSize(item.FileSize);
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}
