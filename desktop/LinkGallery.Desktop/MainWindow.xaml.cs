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
using System.Windows.Threading;
using LinkGallery.Application.Media;
using LinkGallery.Application.Transfers;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;
using LinkGallery.Infrastructure.Media;
using LinkGallery.Infrastructure.Transfers;
using Microsoft.Win32;

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
    private readonly JsonTransferJobStore _transferStore;
    private readonly CurrentTransferMediaSourceResolver _transferSourceResolver = new();
    private readonly PersistentTransferCoordinator _transferCoordinator;
    private readonly DispatcherTimer _transferRefreshTimer;
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
        _transferStore = new JsonTransferJobStore(Path.Combine(dataDirectory, "transfer-jobs.json"));
        _transferCoordinator = new PersistentTransferCoordinator(
            _transferStore,
            _transferSourceResolver,
            new TransferCoordinatorOptions { ComputeSha256 = true },
            localCopies: _localCopies);
        _transferRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _transferRefreshTimer.Tick += OnTransferRefreshTick;
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<MediaRow> TimelineRows { get; } = [];

    public ObservableCollection<TransferRow> TransferRows { get; } = [];

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAddressHint();
        try
        {
            await _transferCoordinator.StartAsync();
            RefreshTransferRows();
            _transferRefreshTimer.Start();
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

        try
        {
            var apiAddress = HttpReadOnlyMediaSource.NormalizeApiAddress(AddressTextBox.Text);
            var endpoint = $"{apiAddress.Host}:{apiAddress.Port}";
            SetLoading(true, $"正在连接 {endpoint}…");
            SetEmptyState($"正在连接 {endpoint}…");
            _connectionCancellation = new CancellationTokenSource();
            var cancellationToken = _connectionCancellation.Token;
            var httpSource = new HttpReadOnlyMediaSource(_httpClient, apiAddress);
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinkGallery",
                "cache");
            _source = new CachingReadOnlyMediaSource(
                httpSource,
                cacheRoot,
                apiAddress.AbsoluteUri);

            var syncProgress = new Progress<MediaSyncProgress>(
                progress => UpdateSyncProgress(endpoint, progress));
            var sync = await _synchronizer.SynchronizeAsync(
                _source,
                syncProgress,
                cancellationToken);
            var device = sync.Device;
            _activeDeviceId = device.Id;
            _transferSourceResolver.SetSource(device.Id, _source);

            ShowDevice(device);
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

    private void UpdateSyncProgress(string endpoint, MediaSyncProgress progress)
    {
        if (progress.Device is not null)
        {
            ShowDevice(progress.Device);
        }

        LoadingProgress.IsIndeterminate = progress.TotalItems is null || progress.TotalItems == 0;
        if (progress.TotalItems > 0)
        {
            LoadingProgress.Value = Math.Min(
                100,
                (double)Math.Min(progress.ItemsReceived, progress.TotalItems.Value) /
                    progress.TotalItems.Value * 100);
        }

        var totalText = progress.TotalItems.HasValue
            ? progress.TotalItems.Value.ToString("N0", CultureInfo.CurrentCulture)
            : "?";
        var mode = progress.WasFullScan ? "完整索引" : "增量更新";
        var status = progress.Stage switch
        {
            MediaSyncStage.Connecting => $"正在连接 {endpoint}…",
            MediaSyncStage.DeviceLoaded =>
                $"已连接 {progress.Device?.Name} · 共 {totalText} 项媒体",
            MediaSyncStage.FetchingPage =>
                $"正在读取第 {progress.PagesFetched + 1:N0} 页 · 已同步 {progress.ItemsReceived:N0}/{totalText}",
            MediaSyncStage.WritingPage =>
                $"正在写入本地索引 · {progress.ItemsReceived:N0}/{totalText} · 第 {progress.PagesFetched:N0} 页",
            MediaSyncStage.Completing =>
                $"正在收尾 {mode} · {progress.ItemsReceived:N0}/{totalText}",
            MediaSyncStage.Completed =>
                $"已完成 {mode} · {progress.ItemsReceived:N0}/{totalText}",
            _ => $"正在同步 {endpoint}…",
        };
        StatusText.Text = status;
        SetEmptyState(status);
    }

    private void ShowDevice(LinkGallery.Domain.Devices.Device device)
    {
        DeviceNameText.Text = device.Name;
        DeviceModelText.Text = string.IsNullOrWhiteSpace(device.Model)
            ? device.Platform
            : $"{device.Model} · {device.Platform}";
        BatteryText.Text = device.BatteryPercent.HasValue
            ? $"电量 {device.BatteryPercent}%"
            : "电量未知";
        MediaCountText.Text = $"共 {device.MediaCount:N0} 项媒体";
        DevicePanel.Visibility = Visibility.Visible;
    }

    private void SetEmptyState(string message)
    {
        TimelineList.Visibility = Visibility.Collapsed;
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
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

    private async void OnImportSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = TimelineList.SelectedItems
            .OfType<MediaRow>()
            .Select(row => row.Item)
            .ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "请先选择一项或多项媒体（可按 Ctrl 或 Shift 多选）";
            return;
        }

        if (_source is null || _source.IsOffline)
        {
            StatusText.Text = "设备离线，连接手机后再开始新的导入";
            return;
        }

        var picker = new OpenFolderDialog
        {
            Title = "选择导入目录",
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        ImportSelectedButton.IsEnabled = false;
        try
        {
            foreach (var item in selected)
            {
                await _transferCoordinator.EnqueueAsync(item, picker.FolderName);
            }

            RefreshTransferRows();
            StatusText.Text = $"已将 {selected.Length:N0} 项加入导入中心";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法加入导入队列：{exception.Message}";
        }
        finally
        {
            ImportSelectedButton.IsEnabled = true;
        }
    }

    private async void OnPauseAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _transferCoordinator.PauseAllAsync();
            RefreshTransferRows();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"暂停失败：{exception.Message}";
        }
    }

    private async void OnResumeAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _transferCoordinator.ResumeAllAsync();
            RefreshTransferRows();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"恢复失败：{exception.Message}";
        }
    }

    private async void OnClearCompletedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _transferCoordinator.ClearCompletedAsync();
            RefreshTransferRows();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"清理失败：{exception.Message}";
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid jobId })
        {
            return;
        }

        try
        {
            await _transferCoordinator.RetryAsync(jobId);
            RefreshTransferRows();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"重试失败：{exception.Message}";
        }
    }

    private void OnTransferRefreshTick(object? sender, EventArgs e) => RefreshTransferRows();

    private void RefreshTransferRows()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = _transferCoordinator.GetJobs();
        var activeIds = jobs.Select(job => job.Id).ToHashSet();
        for (var index = TransferRows.Count - 1; index >= 0; index--)
        {
            if (!activeIds.Contains(TransferRows[index].Id))
            {
                TransferRows.RemoveAt(index);
            }
        }

        foreach (var job in jobs)
        {
            var row = TransferRows.FirstOrDefault(candidate => candidate.Id == job.Id);
            if (row is null)
            {
                row = new TransferRow(job, now);
                TransferRows.Add(row);
            }
            else
            {
                row.Update(job, now);
            }
        }

        var completed = jobs.Count(job => job.Status == TransferStatus.Completed);
        var failed = jobs.Count(job => job.Status == TransferStatus.Failed);
        var remainingBytes = jobs
            .Where(job => !job.IsTerminal)
            .Sum(job => job.TotalBytes - job.BytesTransferred);
        ImportSummaryText.Text = jobs.Count == 0
            ? "暂无任务"
            : $"{completed:N0}/{jobs.Count:N0} 完成 · {failed:N0} 失败 · 剩余 {FormatSize(remainingBytes)}";
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
        _transferRefreshTimer.Stop();
        _transferCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _transferStore.Dispose();
        _thumbnailConcurrency.Dispose();
        _httpClient.Dispose();
        _localCopies.Dispose();
        _mediaIndex.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Disconnect(bool clearTimeline)
    {
        _transferSourceResolver.Clear();
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
        if (isLoading)
        {
            LoadingProgress.IsIndeterminate = true;
            LoadingProgress.Value = 0;
        }

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

    public sealed class TransferRow : INotifyPropertyChanged
    {
        private long _previousBytes;
        private DateTimeOffset _previousUpdate;
        private double _bytesPerSecond;

        public TransferRow(TransferJob job, DateTimeOffset now)
        {
            Id = job.Id;
            FileName = Path.GetFileName(job.DestinationPath);
            _previousBytes = job.BytesTransferred;
            _previousUpdate = now;
            Apply(job);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid Id { get; }

        public string FileName { get; }

        public string StatusText { get; private set; } = "";

        public double ProgressPercent { get; private set; }

        public string ProgressText { get; private set; } = "";

        public string RemainingText { get; private set; } = "";

        public bool CanRetry { get; private set; }

        public void Update(TransferJob job, DateTimeOffset now)
        {
            var seconds = (now - _previousUpdate).TotalSeconds;
            if (job.Status == TransferStatus.Running && seconds > 0)
            {
                var currentSpeed = Math.Max(0, job.BytesTransferred - _previousBytes) / seconds;
                _bytesPerSecond = _bytesPerSecond == 0
                    ? currentSpeed
                    : (_bytesPerSecond * 0.65) + (currentSpeed * 0.35);
            }
            else if (job.Status != TransferStatus.Running)
            {
                _bytesPerSecond = 0;
            }

            _previousBytes = job.BytesTransferred;
            _previousUpdate = now;
            Apply(job);
        }

        private void Apply(TransferJob job)
        {
            StatusText = job.Status switch
            {
                TransferStatus.Pending => "等待",
                TransferStatus.Running => "复制中",
                TransferStatus.Paused => "已暂停",
                TransferStatus.Retrying => "等待重试",
                TransferStatus.Completed => "已完成",
                TransferStatus.Failed => $"失败 · {job.FailureReason}",
                TransferStatus.Cancelled => "已取消",
                _ => job.Status.ToString(),
            };
            ProgressPercent = job.TotalBytes == 0
                ? 100
                : (double)job.BytesTransferred / job.TotalBytes * 100;
            ProgressText =
                $"{FormatSize(job.BytesTransferred)} / {FormatSize(job.TotalBytes)}";
            var remaining = Math.Max(0, job.TotalBytes - job.BytesTransferred);
            RemainingText = job.Status == TransferStatus.Running && _bytesPerSecond > 1
                ? $"{FormatSize((long)_bytesPerSecond)}/s · {FormatDuration(remaining / _bytesPerSecond)}"
                : job.Status == TransferStatus.Completed
                    ? "完成"
                    : $"剩余 {FormatSize(remaining)}";
            CanRetry = job.Status == TransferStatus.Failed;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        private static string FormatDuration(double seconds)
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
