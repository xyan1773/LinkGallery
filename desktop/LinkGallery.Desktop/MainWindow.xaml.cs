using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkGallery.Application.Media;
using LinkGallery.Application.Devices;
using LinkGallery.Application.Transfers;
using LinkGallery.Domain.Devices;
using LinkGallery.Domain.Media;
using LinkGallery.Domain.Transfers;
using LinkGallery.Infrastructure.Media;
using LinkGallery.Infrastructure.Devices;
using LinkGallery.Infrastructure.Transfers;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace LinkGallery.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private const int PageSize = 100;
    private const int DecodedThumbnailCapacity = 128;
    private static readonly JsonSerializerOptions PreferencesJsonOptions = new() { WriteIndented = true };
    private static readonly ThumbnailSize TimelineThumbnailSize = new(256, 256);
    private enum UiLanguage
    {
        English,
        Chinese,
    }

    private enum CloseBehavior
    {
        AskEveryTime,
        HideToTray,
        QuitApp,
    }

    private sealed class AppPreferences
    {
        public string? Language { get; set; }

        public string? CloseBehavior { get; set; }

        public string? DesktopId { get; set; }
    }

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _thumbnailConcurrency = new(6, 6);
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly Dictionary<DecodedThumbnailKey, ImageSource> _decodedThumbnails = [];
    private readonly Queue<DecodedThumbnailKey> _decodedThumbnailOrder = [];
    private readonly SqliteMediaIndex _mediaIndex;
    private readonly BackgroundIndexGate _backgroundIndexGate = new();
    private readonly IncrementalMediaIndexSynchronizer _synchronizer;
    private readonly SqlitePairedDeviceStore _pairedDeviceStore;
    private readonly WindowsAccessTokenStore _accessTokenStore;
    private readonly HttpPairingClient _pairingClient;
    private readonly LocalDeviceDiscovery _localDeviceDiscovery;
    private readonly DiscoveryManager _discoveryManager = new();
    private readonly LocalCopyCatalog _localCopies;
    private readonly ThumbnailCacheReader _thumbnailCache;
    private readonly string _dataDirectory;
    private readonly string _preferencesPath;
    private readonly string _thumbnailCacheDirectory;
    private readonly JsonTransferJobStore _transferStore;
    private readonly CurrentTransferMediaSourceResolver _transferSourceResolver = new();
    private readonly PersistentTransferCoordinator _transferCoordinator;
    private readonly DispatcherTimer _transferRefreshTimer;
    private readonly DispatcherTimer _toastTimer;
    private string _downloadDirectory;
    private bool _preserveAlbumFolders = true;
    private bool _reduceMotion;
    private UiLanguage _language = UiLanguage.English;
    private CloseBehavior _closeBehavior = CloseBehavior.AskEveryTime;
    private string _desktopId = Guid.NewGuid().ToString("N");
    private Forms.NotifyIcon? _notifyIcon;
    private bool _isQuitting;
    private bool _isSelectionMode;
    private bool _transferGatePaused;
    private MediaRow? _viewerRow;
    private double _viewerZoom = 1;
    private CancellationTokenSource? _connectionCancellation;
    private CancellationTokenSource? _backgroundSyncCancellation;
    private CancellationTokenSource? _queryCancellation;
    private CachingReadOnlyMediaSource? _source;
    private string? _activeDeviceId;
    private string? _activeAccessToken;
    private string? _pendingPairingCode;
    private PairedDevice? _activePairedDevice;
    private Uri? _activeApiAddress;
    private string? _remoteNextCursor;
    private bool _hasMoreRemoteItems;
    private bool _hasMoreIndexedItems;
    private int _indexedOffset;
    private bool _isLoadingPage;
    private string _currentPage = "Albums";
    private readonly HashSet<string> _loadedRemoteIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public MainWindow()
    {
        var dataDirectory = ResolveDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        _dataDirectory = dataDirectory;
        _preferencesPath = Path.Combine(dataDirectory, "preferences.json");
        LoadPreferences();
        SavePreferences();
        _mediaIndex = new SqliteMediaIndex(Path.Combine(dataDirectory, "media-index.db"));
        _synchronizer = new IncrementalMediaIndexSynchronizer(
            _mediaIndex,
            gate: _backgroundIndexGate);
        _pairedDeviceStore = new SqlitePairedDeviceStore(Path.Combine(dataDirectory, "devices.db"));
        _accessTokenStore = new WindowsAccessTokenStore(Path.Combine(dataDirectory, "credentials"));
        _pairingClient = new HttpPairingClient(_httpClient);
        _localDeviceDiscovery = new LocalDeviceDiscovery(_httpClient);
        _localCopies = new LocalCopyCatalog(Path.Combine(dataDirectory, "local-copies.json"));
        _thumbnailCacheDirectory = Path.Combine(dataDirectory, "cache", "thumbnails");
        _thumbnailCache = new ThumbnailCacheReader(_thumbnailCacheDirectory);
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
        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1800),
        };
        _toastTimer.Tick += OnToastTimerTick;
        InitializeComponent();
        DataContext = this;
        PopulateDateFilters();
        _downloadDirectory = ResolveDefaultDownloadDirectory();
        UpdateSettingsSummary();
        UpdateSwitchVisuals();
        UpdateSelectionUi();
        ApplyLanguage();
    }

    private bool IsChinese => _language == UiLanguage.Chinese;

    private string L(string english, string chinese) => IsChinese ? chinese : english;

    private void PopulateDateFilters()
    {
        DateFilterComboBox.Items.Clear();
        DateFilterComboBox.Items.Add(new ComboBoxItem { Content = L("All dates", "全部日期"), Tag = "all" });
        var now = DateTime.Today;
        for (var year = now.Year; year >= now.Year - 4; year--)
        {
            DateFilterComboBox.Items.Add(new ComboBoxItem
            {
                Content = IsChinese ? $"{year} 年" : year.ToString(CultureInfo.InvariantCulture),
                Tag = $"year:{year}",
            });
        }
        for (var offset = 0; offset < 24; offset++)
        {
            var month = now.AddMonths(-offset);
            DateFilterComboBox.Items.Add(new ComboBoxItem
            {
                Content = month.ToString(IsChinese ? "yyyy 年 M 月" : "MMMM yyyy", CultureInfo.CurrentCulture),
                Tag = $"month:{month:yyyy-MM}",
            });
        }
        DateFilterComboBox.SelectedIndex = 0;
    }

    private HashSet<MediaType>? GetSelectedMediaTypes() =>
        ((TypeFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "image" => new HashSet<MediaType> { MediaType.Image },
            "video" => new HashSet<MediaType> { MediaType.Video },
            _ => null,
        };

    private (DateTimeOffset? FromInclusive, DateTimeOffset? ToExclusive) GetSelectedDateRange()
    {
        var tag = (DateFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag?.StartsWith("year:", StringComparison.Ordinal) == true &&
            int.TryParse(tag.AsSpan(5), CultureInfo.InvariantCulture, out var year))
        {
            return (LocalBoundary(year, 1), LocalBoundary(year + 1, 1));
        }
        if (tag?.StartsWith("month:", StringComparison.Ordinal) == true &&
            DateTime.TryParseExact(
                tag.AsSpan(6),
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var month))
        {
            var next = month.AddMonths(1);
            return (LocalBoundary(month.Year, month.Month), LocalBoundary(next.Year, next.Month));
        }
        return (null, null);
    }

    private static DateTimeOffset LocalBoundary(int year, int month)
    {
        var local = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void ApplyLanguage()
    {
        ExpandedBrandText.Text = L("Your media, nearby.", "你的媒体，就在附近。");
        NavGalleryLabelText.Text = L("Photos", "照片");
        NavAlbumsLabelText.Text = L("Albums", "相册");
        SidebarSmartAlbumsHeadingText.Text = L("SMART ALBUMS", "智能相册");
        SidebarFavoritesLabelText.Text = L("Favorites", "收藏");
        SidebarVideosLabelText.Text = L("Videos", "视频");
        SidebarScreenshotsLabelText.Text = L("Screenshots", "截图");
        SidebarDeviceAlbumsHeading.Text = L("DEVICE ALBUMS", "设备相册");
        SidebarMyAlbumsHeadingText.Text = L("MY ALBUMS", "我的相册");
        SidebarMyAlbumsEmptyText.Text = L("No custom albums yet", "还没有自定义相册");
        NavDevicesLabelText.Text = L("Devices", "设备");
        NavSettingsLabelText.Text = L("Settings", "设置");
        UpdateOnlineIndicators();

        BackToAlbumsButton.Content = L("‹ Albums", "‹ 相册");
        SearchButton.Content = L("Search", "搜索");
        ImportSelectedButton.Content = _isSelectionMode ? L("Done", "完成") : L("Multi-select", "多选");
        NewAlbumButton.Content = L("New album", "新建相册");
        EnterIpButton.Content = L("Pair device", "配对设备");
        FindDevicesButton.Content = L("Find devices", "查找设备");
        CopyAlbumButton.Content = L("Copy album", "复制相册");

        SmartAlbumsTitleText.Text = L("Smart Albums", "智能相册");
        SeeAllSmartButton.Content = L("See all", "查看全部");
        DeviceAlbumsTitleText.Text = L("Device Albums", "设备相册");
        ManageSourcesButton.Content = L("Manage sources", "管理来源");
        MyAlbumsTitleText.Text = L("My Albums", "我的相册");
        AlbumsPageNewAlbumButton.Content = L("New album", "新建相册");
        MyAlbumsEmptyText.Text = L("No custom albums yet", "还没有自定义相册");
        AlbumFilterAllButton.Content = L("All", "全部");
        AlbumFilterPhotosButton.Content = L("Photos", "照片");
        AlbumFilterVideosButton.Content = L("Videos", "视频");
        AlbumDetailEmptyText.Text = L("No media in this album", "这个相册里没有媒体");

        DevicesEmptyText.Text = L("No connected devices", "没有已连接的设备");
        DeviceCardTitleText.Text = L("Connected device", "已连接设备");
        DeviceCardSubtitleText.Text = L("Connected", "已连接");
        BrowseDevicePhotosButton.Content = L("Browse photos", "浏览照片");
        DisconnectButton.Content = L("Disconnect", "断开连接");
        ForgetDeviceButton.Content = L("Forget device", "忘记设备");
        ManualConnectionTitleText.Text = L("Manual connection", "手动连接");
        ManualConnectionSubtitleText.Text = L("Enter the phone API address, then connect.", "输入手机 API 地址后连接。");
        ConnectButton.Content = L("Connect", "连接");
        CancelManualConnectionButton.Content = L("Cancel", "取消");

        LanguageTitleText.Text = L("Language", "语言");
        LanguageSubtitleText.Text = L("Choose interface language", "选择界面显示语言");
        LanguageEnglishButton.Content = "English";
        LanguageChineseButton.Content = "中文";
        CloseBehaviorTitleText.Text = L("Close button behavior", "关闭按钮行为");
        CloseBehaviorSubtitleText.Text = L(
            "Choose what happens when closing the window",
            "设置点击关闭按钮时的处理方式");
        CloseAskButton.Content = L("Ask", "询问");
        CloseHideButton.Content = L("Hide", "隐藏");
        CloseQuitButton.Content = L("Quit", "退出");
        DownloadFolderTitleText.Text = L("Download folder", "下载文件夹");
        ChooseDownloadFolderButton.Content = L("Choose", "选择");
        PreserveFoldersTitleText.Text = L("Preserve album folders", "保留相册文件夹");
        PreserveFoldersSubtitleText.Text = L("Create destination folders using album names", "复制时按相册名称创建目标文件夹");
        ThumbnailCacheTitleText.Text = L("Thumbnail cache", "缩略图缓存");
        ClearThumbnailCacheButton.Content = L("Clear", "清理");
        ReduceMotionTitleText.Text = L("Reduce motion", "减少动画");
        ReduceMotionSubtitleText.Text = L("Use simpler transitions and remove scale effects", "使用更简单的过渡并移除缩放效果");

        InspectorTypeLabelText.Text = L("Type", "类型");
        InspectorSizeLabelText.Text = L("Size", "大小");
        InspectorResolutionLabelText.Text = L("Resolution", "分辨率");
        InspectorDeviceLabelText.Text = L("Device", "设备");
        ClosePromptTitleText.Text = L("Close LinkGallery?", "关闭 LinkGallery？");
        ClosePromptBodyText.Text = L(
            "Hide the app to the tray so transfers and cache tasks can continue, or quit LinkGallery completely.",
            "可以隐藏到托盘，让复制和缓存任务继续运行；也可以完全退出 LinkGallery。");
        ClosePromptRememberCheckBox.Content = L("Remember my choice", "记住我的选择");
        ClosePromptCancelButton.Content = L("Cancel", "取消");
        ClosePromptQuitButton.Content = L("Quit LinkGallery", "退出 LinkGallery");
        ClosePromptHideButton.Content = L("Hide to tray", "隐藏到托盘");
        UpdateSelectionUi();
        UpdateAddressHint();
        UpdateSettingsSummary();
        UpdateLanguageButtonStyles();
        UpdateCloseBehaviorButtonStyles();
        UpdateTrayMenu();
        UpdatePageSubtitle(_currentPage);
    }

    private void UpdateLanguageButtonStyles()
    {
        LanguageEnglishButton.Style = _language == UiLanguage.English
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
        LanguageChineseButton.Style = _language == UiLanguage.Chinese
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
    }

    private void UpdateCloseBehaviorButtonStyles()
    {
        CloseAskButton.Style = _closeBehavior == CloseBehavior.AskEveryTime
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
        CloseHideButton.Style = _closeBehavior == CloseBehavior.HideToTray
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
        CloseQuitButton.Style = _closeBehavior == CloseBehavior.QuitApp
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
    }

    private void LoadPreferences()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
            {
                return;
            }

            var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_preferencesPath));
            if (Enum.TryParse(preferences?.Language, ignoreCase: true, out UiLanguage language))
            {
                _language = language;
            }

            if (Enum.TryParse(preferences?.CloseBehavior, ignoreCase: true, out CloseBehavior closeBehavior))
            {
                _closeBehavior = closeBehavior;
            }

            if (!string.IsNullOrWhiteSpace(preferences?.DesktopId))
            {
                _desktopId = preferences.DesktopId;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var preferences = new AppPreferences
            {
                Language = _language.ToString(),
                CloseBehavior = _closeBehavior.ToString(),
                DesktopId = _desktopId,
            };
            var json = JsonSerializer.Serialize(preferences, PreferencesJsonOptions);
            File.WriteAllText(_preferencesPath, json);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveDataDirectory()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("LINKGALLERY_E2E"),
                "1",
                StringComparison.Ordinal) &&
            Environment.GetEnvironmentVariable("LINKGALLERY_E2E_DATA_DIRECTORY")
                is { Length: > 0 } e2eDirectory)
        {
            return Path.GetFullPath(e2eDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkGallery");
    }

    private static string ResolveDefaultDownloadDirectory()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("LINKGALLERY_E2E"),
                "1",
                StringComparison.Ordinal) &&
            Environment.GetEnvironmentVariable("LINKGALLERY_E2E_IMPORT_DIRECTORY")
                is { Length: > 0 } e2eImportDirectory)
        {
            return Path.GetFullPath(e2eImportDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "LinkGallery");
    }

    private void UpdateSettingsSummary()
    {
        DownloadFolderValueText.Text = _downloadDirectory;
        var (count, bytes) = GetThumbnailCacheStats();
        ThumbnailCacheValueText.Text = count == 0
            ? L("No cached previews", "没有缓存预览")
            : L(
                $"{FormatSize(bytes)} · {count:N0} cached previews",
                $"{FormatSize(bytes)} · {count:N0} 个缓存预览");
    }

    private void UpdateOnlineIndicators()
    {
        var online = _source is { IsOffline: false };
        NavDevicesOnlineDot.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        StatusOnlineDot.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        DeviceStatusText.Text = online ? L("Online", "在线") : L("Offline", "离线");
    }

    private void UpdateSwitchVisuals()
    {
        FolderSwitchTrack.Background = _preserveAlbumFolders
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xE3))
            : new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6));
        FolderSwitchThumb.HorizontalAlignment = _preserveAlbumFolders
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;

        MotionSwitchTrack.Background = _reduceMotion
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xE3))
            : new SolidColorBrush(Color.FromRgb(0xD1, 0xD1, 0xD6));
        MotionSwitchThumb.HorizontalAlignment = _reduceMotion
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
    }

    private (int Count, long Bytes) GetThumbnailCacheStats()
    {
        if (!Directory.Exists(_thumbnailCacheDirectory))
        {
            return (0, 0);
        }

        try
        {
            var count = 0;
            var bytes = 0L;
            foreach (var path in Directory.EnumerateFiles(_thumbnailCacheDirectory, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                count++;
                bytes += info.Length;
            }

            return (count, bytes);
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    public ObservableCollection<MediaRow> TimelineRows { get; } = [];

    public ObservableCollection<MediaGroupRow> TimelineGroups { get; } = [];

    public ObservableCollection<MediaRow> AlbumDetailRows { get; } = [];

    public ObservableCollection<MediaGroupRow> AlbumDetailGroups { get; } = [];

    public ObservableCollection<AlbumRow> AlbumRows { get; } = [];

    public ObservableCollection<AlbumRow> SmartAlbumRows { get; } = [];

    public ObservableCollection<AlbumRow> DeviceAlbumRows { get; } = [];

    public ObservableCollection<AlbumRow> MyAlbumRows { get; } = [];

    public ObservableCollection<AlbumRow> SidebarDeviceAlbumRows { get; } = [];

    public ObservableCollection<AlbumRow> SidebarMyAlbumRows { get; } = [];

    public ObservableCollection<TransferRow> TransferRows { get; } = [];

    private AlbumRow? _activeAlbum;
    private string _activeAlbumFilter = "All";

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAddressHint();
        ShowPage("Albums");
        try
        {
            await _transferCoordinator.StartAsync();
            await _pairedDeviceStore.InitializeAsync();
            RefreshTransferRows();
            _transferRefreshTimer.Start();
            await LoadIndexedPageAsync(
                reset: true,
                L("No media in local cache", "本地缓存中还没有媒体"),
                CancellationToken.None);
            UpdateIndexedStatus();
            var savedDevice = (await _pairedDeviceStore.ListPairedDevicesAsync(CancellationToken.None))
                .FirstOrDefault(device =>
                    device.AutoConnect &&
                    !string.IsNullOrWhiteSpace(device.LastHost) &&
                    device.LastPort.HasValue);
            if (savedDevice is not null)
            {
                AddressTextBox.Text = $"{savedDevice.LastHost}:{savedDevice.LastPort}";
                OnConnectClick(ConnectButton, new RoutedEventArgs());
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = L(
                $"Cannot read local index: {exception.Message}",
                $"无法读取本地索引：{exception.Message}");
        }
    }

    private void OnAddressTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (AddressHintText is not null)
        {
            UpdateAddressHint();
        }
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void ShowClosePrompt()
    {
        ClosePromptRememberCheckBox.IsChecked = false;
        ClosePromptModal.Visibility = Visibility.Visible;
    }

    private void OnClosePromptCancelClick(object sender, RoutedEventArgs e) =>
        ClosePromptModal.Visibility = Visibility.Collapsed;

    private void OnClosePromptHideClick(object sender, RoutedEventArgs e)
    {
        RememberCloseBehaviorIfRequested(CloseBehavior.HideToTray);
        ClosePromptModal.Visibility = Visibility.Collapsed;
        HideToTray();
    }

    private void OnClosePromptQuitClick(object sender, RoutedEventArgs e)
    {
        RememberCloseBehaviorIfRequested(CloseBehavior.QuitApp);
        ClosePromptModal.Visibility = Visibility.Collapsed;
        QuitApplication();
    }

    private void RememberCloseBehaviorIfRequested(CloseBehavior behavior)
    {
        if (ClosePromptRememberCheckBox.IsChecked != true)
        {
            return;
        }

        _closeBehavior = behavior;
        SavePreferences();
        UpdateCloseBehaviorButtonStyles();
    }

    private void HideToTray()
    {
        EnsureNotifyIcon();
        Hide();
        ShowToast(L("LinkGallery is hidden in the tray", "LinkGallery 已隐藏到托盘"));
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApplication()
    {
        _isQuitting = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Close();
    }

    private void EnsureNotifyIcon()
    {
        if (_notifyIcon is null)
        {
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = ResolveTrayIcon(),
                Text = "LinkGallery",
                Visible = true,
            };
            _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }

        _notifyIcon.Visible = true;
        UpdateTrayMenu();
    }

    private static Drawing.Icon ResolveTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "photo_link_icon.ico");
        return File.Exists(iconPath) ? new Drawing.Icon(iconPath) : Drawing.SystemIcons.Application;
    }

    private void UpdateTrayMenu()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L("Show LinkGallery", "显示 LinkGallery"), null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(L("Quit", "退出"), null, (_, _) => Dispatcher.Invoke(QuitApplication));
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SearchPlaceholderText is null)
        {
            return;
        }

        SearchPlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateAddressHint()
    {
        var defaultHint = L(
            "Emulator: run adb forward first, then enter 127.0.0.1:39570. Do not use the emulator 10.0.2.x address.",
            "模拟器：先执行 adb forward，再输入 127.0.0.1:39570；不要输入模拟器显示的 10.0.2.x。");
        if (string.IsNullOrWhiteSpace(AddressTextBox.Text))
        {
            AddressHintText.Text = defaultHint;
            AddressHintText.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        try
        {
            var address = HttpReadOnlyMediaSource.NormalizeApiAddress(AddressTextBox.Text);
            var couldBeEmulatorNat =
                HttpReadOnlyMediaSource.IsPotentialAndroidEmulatorNatAddress(address);
            AddressHintText.Text = couldBeEmulatorNat
                ? L(
                    "Hint: 10.0.2.x is usually the emulator NAT address. Use adb forward and 127.0.0.1 for emulator; real Wi-Fi phones can still connect directly.",
                    "提示：10.0.2.x 常见于模拟器内部 NAT，模拟器应使用 adb forward 和 127.0.0.1；若这是真实手机的 Wi-Fi 地址，仍可直接连接。")
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
        var rawAddress = AddressTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            SetManualConnectionOpen(true);
            StatusText.Text = L("Enter a phone API address first.", "请先输入手机 API 地址。");
            ShowToast(L("Enter IP manually first", "请先手动输入 IP"));
            AddressTextBox.Focus();
            return;
        }

        Uri apiAddress;
        try
        {
            apiAddress = HttpReadOnlyMediaSource.NormalizeApiAddress(rawAddress);
        }
        catch (FormatException exception)
        {
            StatusText.Text = exception.Message;
            SetManualConnectionOpen(true);
            ShowToast(L("Invalid phone address", "手机地址无效"));
            AddressTextBox.Focus();
            return;
        }

        Disconnect(clearTimeline: true);
        SetManualConnectionOpen(true);

        try
        {
            var endpoint = $"{apiAddress.Host}:{apiAddress.Port}";
            SetLoading(true, L($"Connecting to {endpoint}...", $"正在连接 {endpoint}…"));
            SetEmptyState(L($"Connecting to {endpoint}...", $"正在连接 {endpoint}…"));
            _connectionCancellation = new CancellationTokenSource();
            var cancellationToken = _connectionCancellation.Token;
            var pairingCode = _pendingPairingCode;
            _pendingPairingCode = null;
            var authorization = await ResolveAuthorizationAsync(
                apiAddress,
                cancellationToken,
                pairingCode);
            var httpSource = new HttpReadOnlyMediaSource(
                _httpClient,
                apiAddress,
                accessToken: authorization.AccessToken);
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinkGallery",
                "cache");
            _source = new CachingReadOnlyMediaSource(
                httpSource,
                cacheRoot,
                apiAddress.AbsoluteUri,
                mediaIndex: _mediaIndex);

            var device = await GetDeviceInfoWithTimeoutAsync(_source, cancellationToken);
            _activeDeviceId = device.Id;
            _activeAccessToken = authorization.AccessToken;
            _activePairedDevice = authorization.PairedDevice;
            _activeApiAddress = apiAddress;
            _transferSourceResolver.SetSource(device.Id, _source);
            authorization.PairedDevice.LastConnectedAt = DateTimeOffset.UtcNow;
            authorization.PairedDevice.LastSeenAt = DateTimeOffset.UtcNow;
            authorization.PairedDevice.Status = PairedDeviceStatus.Online;
            authorization.PairedDevice.LastHost = apiAddress.Host;
            authorization.PairedDevice.LastPort = apiAddress.Port;
            authorization.PairedDevice.AutoConnect = true;
            await _pairedDeviceStore.UpsertPairedDeviceAsync(
                authorization.PairedDevice,
                cancellationToken);
            await _pairedDeviceStore.UpsertAddressAsync(
                new DeviceAddress
                {
                    DeviceId = authorization.PairedDevice.DeviceId,
                    Host = apiAddress.Host,
                    Port = apiAddress.Port,
                    Source = DeviceAddressSource.Manual,
                    LastSuccessAt = DateTimeOffset.UtcNow,
                },
                cancellationToken);

            ShowDevice(device);
            DisconnectButton.IsEnabled = true;
            ForgetDeviceButton.IsEnabled = true;
            StatusText.Text = L(
                $"Connected to {device.Name} · Loading first media page",
                $"已连接 {device.Name} · 正在加载第一页媒体");
            SetEmptyState(L("Loading first media page...", "正在加载第一页媒体…"));

            await LoadInitialRemotePageAsync(_source, cancellationToken);
            SetLoading(false);
            SetManualConnectionOpen(false);
            ShowPage("Gallery");
            StartBackgroundSync(_source, endpoint);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("Connection cancelled", "连接已取消");
            SetManualConnectionOpen(true);
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
            SetManualConnectionOpen(true);
            await LoadIndexedPageAsync(
                reset: true,
                L("Phone unavailable and local cache is empty", "手机当前不可用，本地缓存中还没有媒体"),
                CancellationToken.None);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async Task<DeviceAuthorization> ResolveAuthorizationAsync(
        Uri apiAddress,
        CancellationToken cancellationToken,
        string? verificationCode = null)
    {
        var publicInfo = await new HttpPublicDeviceInfoClient(_httpClient)
            .GetAsync(apiAddress, cancellationToken);
        var paired = (await _pairedDeviceStore.ListPairedDevicesAsync(cancellationToken))
            .FirstOrDefault(device =>
                string.Equals(device.DeviceId, publicInfo.DeviceId, StringComparison.Ordinal));
        if (paired is not null)
        {
            if (!string.Equals(
                    paired.CertificateFingerprint,
                    publicInfo.CertificateFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    L(
                        "The device identity changed. Remove the saved pairing before reconnecting.",
                        "设备身份已变化，请先移除保存的配对后再重新连接。"));
            }

            var savedToken = await _accessTokenStore.ReadAsync(
                paired.CredentialKey,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(savedToken))
            {
                return new DeviceAuthorization(savedToken, paired);
            }
        }

        if (!publicInfo.PairingAvailable)
        {
            throw new InvalidOperationException(
                L(
                    "Open the Devices page on the phone, then scan the QR code or show a six-digit code.",
                    "请在手机 LinkGallery 的设备页扫描二维码，或显示六位配对码。"));
        }

        var identity = new PairingIdentity(
            _desktopId,
            Environment.MachineName,
            "Windows",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
        var session = await _pairingClient.StartAsync(apiAddress, identity, cancellationToken);
        if (string.IsNullOrWhiteSpace(verificationCode))
        {
            var dialog = new PairingCodeWindow(session.CodeLength) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                throw new OperationCanceledException("Pairing was cancelled.", cancellationToken);
            }
            verificationCode = dialog.VerificationCode;
        }

        var credential = await _pairingClient.ConfirmAsync(
            apiAddress,
            session.PairingSessionId,
            verificationCode,
            cancellationToken);
        var credentialKey = await _accessTokenStore.SaveAsync(
            credential.AccessToken,
            cancellationToken);
        var device = new PairedDevice
        {
            DeviceId = publicInfo.DeviceId,
            DisplayName = publicInfo.DeviceName,
            Manufacturer = publicInfo.Manufacturer,
            Model = publicInfo.Model,
            IdentityPublicKey = publicInfo.CertificateFingerprint,
            CertificateFingerprint = publicInfo.CertificateFingerprint,
            CredentialKey = credentialKey,
            LastHost = apiAddress.Host,
            LastPort = apiAddress.Port,
            LastInstanceId = publicInfo.InstanceId,
            LastSeenAt = DateTimeOffset.UtcNow,
            LastConnectedAt = DateTimeOffset.UtcNow,
            AutoConnect = true,
            Status = PairedDeviceStatus.Online,
        };
        await _pairedDeviceStore.UpsertPairedDeviceAsync(device, cancellationToken);
        return new DeviceAuthorization(credential.AccessToken, device);
    }

    private sealed record DeviceAuthorization(string AccessToken, PairedDevice PairedDevice);

    private static async Task<LinkGallery.Domain.Devices.Device> GetDeviceInfoWithTimeoutAsync(
        CachingReadOnlyMediaSource source,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await source.GetDeviceInfoAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaSourceTimeoutException("设备信息请求超时。", exception);
        }
    }

    private async Task LoadInitialRemotePageAsync(
        CachingReadOnlyMediaSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var page = await source.GetMediaPageAsync(
                new MediaQuery(Limit: PageSize),
                timeout.Token);

            TimelineRows.Clear();
            TimelineGroups.Clear();
            RefreshAlbumRows();
            _loadedRemoteIds.Clear();
            AppendTimelineItems(DeduplicateRemoteItems(page.Items));
            _remoteNextCursor = page.NextCursor;
            _hasMoreRemoteItems = page.HasMore || page.NextCursor is not null;
            _hasMoreIndexedItems = false;
            UpdateTimelineFooter();
            TimelineScrollViewer.Visibility = TimelineRows.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmptyText.Text = L("No photos or videos are available on the phone", "手机中没有可显示的照片或视频");
            EmptyText.Visibility = TimelineRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusText.Text = page.Items.Count == 0
                ? L("Connected · No media available on the phone", "已连接 · 手机中没有可显示的媒体")
                : L(
                    $"Connected · Showing latest {page.Items.Count:N0} items · Index sync continues in background",
                    $"已连接 · 已显示最新 {page.Items.Count:N0} 项 · 后台继续同步索引");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ShowInitialPageDegradedAsync(L(
                "Connected · First media page timed out; index sync continues in background",
                "已连接 · 第一页媒体加载超时，后台继续同步索引"));
        }
        catch (Exception exception)
        {
            await ShowInitialPageDegradedAsync(
                L(
                    $"Connected · First media page failed: {DescribeConnectionStageError(exception)}",
                    $"已连接 · 第一页媒体加载失败：{DescribeConnectionStageError(exception)}"));
        }
    }

    private async Task ShowInitialPageDegradedAsync(string message)
    {
        _remoteNextCursor = null;
        _hasMoreRemoteItems = false;
        _loadedRemoteIds.Clear();
        UpdateTimelineFooter();
        StatusText.Text = message;
        await LoadIndexedPageAsync(
            reset: true,
            L("First page failed and local cache is empty", "第一页加载失败，且本地缓存中还没有媒体"),
            CancellationToken.None);
    }

    private void StartBackgroundSync(CachingReadOnlyMediaSource source, string endpoint)
    {
        _backgroundSyncCancellation?.Cancel();
        _backgroundSyncCancellation?.Dispose();
        _backgroundSyncCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _connectionCancellation?.Token ?? CancellationToken.None);
        var progress = new Progress<MediaSyncProgress>(
            update => UpdateBackgroundSyncProgress(endpoint, update));
        _ = SynchronizeInBackgroundAsync(source, progress, _backgroundSyncCancellation.Token);
    }

    private async Task SynchronizeInBackgroundAsync(
        CachingReadOnlyMediaSource source,
        IProgress<MediaSyncProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var sync = await _synchronizer.SynchronizeAsync(source, progress, cancellationToken);
            await RefreshDeviceAlbumsFromIndexAsync(cancellationToken);
            var syncMode = sync.WasFullScan ? L("full index", "完整索引") : L("incremental update", "增量更新");
            StatusText.Text = L(
                $"Connected · {syncMode} {sync.ItemsReceived:N0} items ({sync.PagesFetched:N0} pages) · Showing {TimelineRows.Count:N0} items",
                $"已连接 · {syncMode} {sync.ItemsReceived:N0} 项（{sync.PagesFetched:N0} 页） · 已显示 {TimelineRows.Count:N0} 项");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = L(
                $"Connected · Background index sync failed: {DescribeConnectionStageError(exception)}",
                $"已连接 · 后台索引同步失败：{DescribeConnectionStageError(exception)}");
        }
    }

    private void UpdateBackgroundSyncProgress(string endpoint, MediaSyncProgress progress)
    {
        if (progress.Device is not null)
        {
            ShowDevice(progress.Device);
        }

        var totalText = progress.TotalItems.HasValue
            ? progress.TotalItems.Value.ToString("N0", CultureInfo.CurrentCulture)
            : "?";
        var mode = progress.WasFullScan ? L("full index", "完整索引") : L("incremental update", "增量更新");
        StatusText.Text = progress.Stage switch
        {
            MediaSyncStage.Connecting => L(
                $"Showing first page · Connecting to {endpoint} in background...",
                $"已显示第一页 · 后台连接 {endpoint}…"),
            MediaSyncStage.DeviceLoaded =>
                L(
                    $"Connected to {progress.Device?.Name} · Preparing to sync {totalText} media items",
                    $"已连接 {progress.Device?.Name} · 后台准备同步 {totalText} 项媒体"),
            MediaSyncStage.FetchingPage =>
                L(
                    $"Showing first page · Reading page {progress.PagesFetched + 1:N0} · {progress.ItemsReceived:N0}/{totalText}",
                    $"已显示第一页 · 后台读取第 {progress.PagesFetched + 1:N0} 页 · {progress.ItemsReceived:N0}/{totalText}"),
            MediaSyncStage.WritingPage =>
                L(
                    $"Showing first page · Writing index · {progress.ItemsReceived:N0}/{totalText}",
                    $"已显示第一页 · 后台写入索引 · {progress.ItemsReceived:N0}/{totalText}"),
            MediaSyncStage.Completing =>
                L(
                    $"Showing first page · Finishing {mode} · {progress.ItemsReceived:N0}/{totalText}",
                    $"已显示第一页 · 后台收尾 {mode} · {progress.ItemsReceived:N0}/{totalText}"),
            MediaSyncStage.Completed =>
                L(
                    $"Showing first page · Completed {mode} · {progress.ItemsReceived:N0}/{totalText}",
                    $"已显示第一页 · 后台完成 {mode} · {progress.ItemsReceived:N0}/{totalText}"),
            _ => L(
                $"Showing first page · Syncing {endpoint} in background...",
                $"已显示第一页 · 后台同步 {endpoint}…"),
        };
    }

    private string DescribeConnectionStageError(Exception exception) =>
        exception switch
        {
            MediaSourceTimeoutException => L("request timed out", "请求超时"),
            MediaSourceConnectionException
                { Failure: MediaSourceConnectionFailure.ConnectionRefused } => L("phone service refused connection", "手机服务拒绝连接"),
            MediaSourceConnectionException
                { Failure: MediaSourceConnectionFailure.NetworkUnreachable } => L("network unreachable", "网络不可达"),
            MediaSourceConnectionException => L("cannot connect to phone", "无法连接手机"),
            MediaSourceProtocolException => L($"protocol error: {exception.Message}", $"协议错误：{exception.Message}"),
            MediaSourceHttpException { StatusCode: HttpStatusCode.Forbidden } => L("phone has not granted media permission", "手机未授予媒体读取权限"),
            MediaSourceHttpException => L($"phone returned an error: {exception.Message}", $"手机返回错误：{exception.Message}"),
            HttpRequestException => L("network request failed", "网络请求失败"),
            _ => exception.Message,
        };

    private void ShowDevice(LinkGallery.Domain.Devices.Device device)
    {
        DeviceNameText.Text = device.Name;
        DeviceModelText.Text = string.IsNullOrWhiteSpace(device.Model)
            ? device.Platform
            : $"{device.Model} · {device.Platform}";
        BatteryText.Text = device.BatteryPercent.HasValue
            ? L($"Battery {device.BatteryPercent}%", $"电量 {device.BatteryPercent}%")
            : L("Battery unknown", "电量未知");
        MediaCountText.Text = L($"{device.MediaCount:N0} media items", $"共 {device.MediaCount:N0} 项媒体");
        UpdateOnlineIndicators();
        SyncStateText.Text = L($"{device.MediaCount:N0} media", $"{device.MediaCount:N0} 项媒体");
        DevicePanel.Visibility = Visibility.Visible;
        DeviceCardsGrid.Visibility = Visibility.Visible;
        ConnectedDeviceCard.Visibility = Visibility.Visible;
        DevicesEmptyText.Visibility = Visibility.Collapsed;
        DeviceCardTitleText.Text = device.Name;
        DeviceCardSubtitleText.Text = string.IsNullOrWhiteSpace(device.Model)
            ? L($"Connected · {device.MediaCount:N0} items", $"已连接 · {device.MediaCount:N0} 项")
            : L($"{device.Model} · Connected · {device.MediaCount:N0} items", $"{device.Model} · 已连接 · {device.MediaCount:N0} 项");
    }

    private void SetEmptyState(string message)
    {
        TimelineScrollViewer.Visibility = Visibility.Collapsed;
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
    }

    private async Task LoadIndexedPageAsync(
        bool reset,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        await _queryGate.WaitAsync(cancellationToken);
        if (!reset && !_hasMoreIndexedItems)
        {
            _queryGate.Release();
            return;
        }

        _isLoadingPage = true;
        try
        {
            if (reset)
            {
                TimelineRows.Clear();
                TimelineGroups.Clear();
                RefreshAlbumRows();
                _hasMoreIndexedItems = true;
                _indexedOffset = 0;
            }

            var (fromInclusive, toExclusive) = GetSelectedDateRange();
            var items = await _mediaIndex.SearchAsync(
                new MediaIndexQuery(
                    DeviceId: _activeDeviceId,
                    SearchText: SearchTextBox.Text,
                    Types: GetSelectedMediaTypes(),
                    FromInclusive: fromInclusive,
                    ToExclusive: toExclusive,
                    Limit: PageSize,
                    Offset: _indexedOffset),
                cancellationToken);
            _indexedOffset += items.Count;
            var visibleItems = ShouldRequireCachedThumbnailForIndexedMedia()
                ? items.Where(HasCachedThumbnail).ToArray()
                : items;
            AppendTimelineItems(visibleItems);
            await RefreshDeviceAlbumsFromIndexAsync(cancellationToken);
            _hasMoreIndexedItems = items.Count == PageSize;
            UpdateTimelineFooter();
            TimelineScrollViewer.Visibility = TimelineRows.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmptyText.Text = TimelineRows.Count == 0 && ShouldRequireCachedThumbnailForIndexedMedia()
                ? "No cached thumbnails yet"
                : emptyMessage;
            EmptyText.Visibility = TimelineRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _isLoadingPage = false;
            _queryGate.Release();
        }
    }

    private void AppendTimelineItems(IReadOnlyList<MediaItem> items)
    {
        var previousDate = TimelineRows.LastOrDefault()?.Item.TakenAt.LocalDateTime.Date;
        foreach (var item in items)
        {
            var date = item.TakenAt.LocalDateTime.Date;
            var dateGroup = IsChinese
                ? date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)
                : date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            var dateHeader = previousDate != date ? dateGroup : null;
            TimelineRows.Add(new MediaRow(item, dateHeader, dateGroup));
            previousDate = date;
        }

        RefreshTimelineGroups();
        RefreshAlbumRows();
    }

    private void RefreshTimelineGroups() =>
        RefreshMediaGroups(TimelineRows, TimelineGroups);

    private void RefreshAlbumDetailGroups() =>
        RefreshMediaGroups(AlbumDetailRows, AlbumDetailGroups);

    private static void RefreshMediaGroups(
        IEnumerable<MediaRow> rows,
        ObservableCollection<MediaGroupRow> target)
    {
        target.Clear();
        foreach (var group in rows
                     .OrderByDescending(static row => row.Item.TakenAt)
                     .GroupBy(static row => row.DateGroup))
        {
            target.Add(new MediaGroupRow(group.Key, group));
        }
    }

    private static bool ShouldRequireCachedThumbnailForIndexedMedia() => false;

    private bool HasCachedThumbnail(MediaItem item)
    {
        try
        {
            return _thumbnailCache.IsThumbnailCached(item, TimelineThumbnailSize);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RefreshAlbumRows()
    {
        var mediaItems = TimelineRows.Select(static row => row.Item).ToArray();
        var albums = mediaItems
            .GroupBy(
                static item => string.IsNullOrWhiteSpace(item.AlbumName)
                    ? "Unsorted"
                    : item.AlbumName,
                StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => CreateAlbumRow(
                group.Key,
                group.Count(),
                group.Count(static item => item.Type == MediaType.Image),
                group.Count(static item => item.Type == MediaType.Video),
                AlbumCoverBrush(group.Key),
                "Device"))
            .ToArray();

        AlbumRows.Clear();
        SmartAlbumRows.Clear();
        DeviceAlbumRows.Clear();
        MyAlbumRows.Clear();
        SidebarDeviceAlbumRows.Clear();
        SidebarMyAlbumRows.Clear();

        SmartAlbumRows.Add(CreateAlbumRow(
            L("Favorites", "收藏"),
            0,
            0,
            0,
            new SolidColorBrush(Color.FromRgb(0xB7, 0xC9, 0xD6)),
            "SmartFavorites"));

        var isConnected = _source is { IsOffline: false };
        if (isConnected)
        {
            SmartAlbumRows.Add(CreateAlbumRow(
                L("Videos", "视频"),
                mediaItems.Count(static item => item.Type == MediaType.Video),
                0,
                mediaItems.Count(static item => item.Type == MediaType.Video),
                new SolidColorBrush(Color.FromRgb(0xD8, 0xB7, 0xA5)),
                "SmartVideos"));
            SmartAlbumRows.Add(CreateAlbumRow(
                L("Screenshots", "截图"),
                CountMatches(mediaItems, "screenshot"),
                0,
                0,
                new SolidColorBrush(Color.FromRgb(0xC5, 0xB2, 0xD9)),
                "SmartScreenshots"));
            SmartAlbumRows.Add(CreateAlbumRow(
                L("Recently Added", "最近添加"),
                mediaItems.Count(static item => item.TakenAt >= DateTimeOffset.Now.AddDays(-30)),
                0,
                0,
                new SolidColorBrush(Color.FromRgb(0xB7, 0xC9, 0xD6)),
                "SmartRecent"));

            foreach (var album in albums)
            {
                AlbumRows.Add(album);
                DeviceAlbumRows.Add(album);
            }

            foreach (var album in albums.Take(3))
            {
                SidebarDeviceAlbumRows.Add(album);
            }
        }

        SidebarVideosButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        SidebarScreenshotsButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        SidebarDeviceAlbumsHeading.Visibility = isConnected && SidebarDeviceAlbumRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SidebarDeviceAlbumsList.Visibility = isConnected && SidebarDeviceAlbumRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceAlbumsHeader.Visibility = isConnected && DeviceAlbumRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceAlbumsList.Visibility = isConnected && DeviceAlbumRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MyAlbumsEmptyText.Visibility = MyAlbumRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SidebarMyAlbumsEmptyButton.Visibility = SidebarMyAlbumRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumBadgeText.Text = (SmartAlbumRows.Count + DeviceAlbumRows.Count + MyAlbumRows.Count).ToString(CultureInfo.InvariantCulture);
    }

    private AlbumRow CreateAlbumRow(
        string name,
        int count,
        int imageCount,
        int videoCount,
        System.Windows.Media.Brush? coverBrush = null,
        string kind = "Device",
        string? albumId = null,
        string? relativePath = null) =>
        new(
            name,
            count,
            imageCount,
            videoCount,
            coverBrush,
            kind,
            count == 1
                ? L("1 item", "1 项")
                : L($"{count:N0} items", $"{count:N0} 项"),
            L($"{imageCount:N0} photos · {videoCount:N0} videos", $"{imageCount:N0} 张照片 · {videoCount:N0} 个视频"),
            albumId,
            relativePath);

    private async Task RefreshDeviceAlbumsFromIndexAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_activeDeviceId)) return;
        var indexedAlbums = await _mediaIndex.GetAlbumsAsync(
            _activeDeviceId,
            searchText: null,
            limit: 500,
            offset: 0,
            cancellationToken);
        AlbumRows.Clear();
        DeviceAlbumRows.Clear();
        SidebarDeviceAlbumRows.Clear();
        foreach (var indexed in indexedAlbums)
        {
            var row = CreateAlbumRow(
                indexed.DisplayName,
                indexed.MediaCount,
                indexed.PhotoCount,
                indexed.VideoCount,
                AlbumCoverBrush(indexed.AlbumId),
                "Device",
                indexed.AlbumId,
                indexed.RelativePath);
            AlbumRows.Add(row);
            DeviceAlbumRows.Add(row);
            if (SidebarDeviceAlbumRows.Count < 3)
            {
                SidebarDeviceAlbumRows.Add(row);
            }
        }
        var visibility = DeviceAlbumRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SidebarDeviceAlbumsHeading.Visibility = visibility;
        SidebarDeviceAlbumsList.Visibility = visibility;
        DeviceAlbumsHeader.Visibility = visibility;
        DeviceAlbumsList.Visibility = visibility;
        AlbumBadgeText.Text =
            (SmartAlbumRows.Count + DeviceAlbumRows.Count + MyAlbumRows.Count)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static int CountMatches(MediaItem[] mediaItems, string text) =>
        mediaItems.Count(item => MatchesText(item, text));

    private static bool MatchesText(MediaItem item, string text) =>
        (item.AlbumName?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (item.RelativePath?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (item.SourceApplication?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        item.FileName.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private static SolidColorBrush AlbumCoverBrush(string key)
    {
        Color[] palette =
        [
            Color.FromRgb(0xB7, 0xC9, 0xD6),
            Color.FromRgb(0xD8, 0xB7, 0xA5),
            Color.FromRgb(0xA4, 0xBC, 0xA5),
            Color.FromRgb(0xC5, 0xB2, 0xD9),
            Color.FromRgb(0xDB, 0xC9, 0x87),
            Color.FromRgb(0x9D, 0xBB, 0xCA),
        ];

        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
        return new SolidColorBrush(palette[Math.Abs(hash % palette.Length)]);
    }

    private List<MediaItem> DeduplicateRemoteItems(IReadOnlyList<MediaItem> items)
    {
        var unique = new List<MediaItem>(items.Count);
        foreach (var item in items)
        {
            if (_loadedRemoteIds.Add(item.RemoteId))
            {
                unique.Add(item);
            }
        }

        return unique;
    }

    private async void OnTimelineItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaRow row } ||
            row.Thumbnail is not null)
        {
            return;
        }

        var decodedKey = DecodedThumbnailKey.Create(row.Item, TimelineThumbnailSize);
        if (_decodedThumbnails.TryGetValue(decodedKey, out var decoded))
        {
            row.Thumbnail = decoded;
            return;
        }

        if (row.IsThumbnailLoading)
        {
            if (row.ThumbnailLoadCancellation?.IsCancellationRequested != true)
            {
                return;
            }

            row.IsThumbnailLoading = false;
        }

        row.IsThumbnailLoading = true;
        row.ThumbnailLoadCancellation?.Dispose();
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _connectionCancellation?.Token ?? CancellationToken.None);
        row.ThumbnailLoadCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;
        try
        {
            await _thumbnailConcurrency.WaitAsync(cancellationToken);
            try
            {
                var image = await TryLoadCachedThumbnailAsync(row.Item, cancellationToken);
                if (image is null && _source is { IsOffline: false } source)
                {
                    await using var stream = await source.OpenThumbnailAsync(
                        row.Item.RemoteId,
                        TimelineThumbnailSize,
                        cancellationToken);
                    image = DecodeBitmap(stream, TimelineThumbnailSize.Width);
                }

                if (image is not null)
                {
                    RememberDecodedThumbnail(decodedKey, image);
                    row.Thumbnail = image;
                }
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
            // The UI stays blank/neutral when a thumbnail is unavailable.
        }
        finally
        {
            if (ReferenceEquals(row.ThumbnailLoadCancellation, loadCancellation))
            {
                row.ThumbnailLoadCancellation = null;
                row.IsThumbnailLoading = false;
            }

            loadCancellation.Dispose();
        }
    }

    private void RememberDecodedThumbnail(DecodedThumbnailKey key, ImageSource image)
    {
        if (!_decodedThumbnails.TryAdd(key, image)) return;
        _decodedThumbnailOrder.Enqueue(key);
        while (_decodedThumbnailOrder.Count > DecodedThumbnailCapacity)
        {
            _decodedThumbnails.Remove(_decodedThumbnailOrder.Dequeue());
        }
    }

    private async Task<ImageSource?> TryLoadCachedThumbnailAsync(
        MediaItem item,
        CancellationToken cancellationToken)
    {
        await using var stream = await _thumbnailCache.OpenCachedThumbnailAsync(
            item,
            TimelineThumbnailSize,
            cancellationToken);
        return stream is null ? null : DecodeBitmap(stream, TimelineThumbnailSize.Width);
    }

    private static BitmapImage DecodeBitmap(Stream stream, int decodePixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnTimelineItemUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaRow row } &&
            row.Thumbnail is null &&
            row.IsThumbnailLoading)
        {
            row.ThumbnailLoadCancellation?.Cancel();
        }
    }

    private void OnTimelineDoubleClick(object sender, MouseButtonEventArgs e) =>
        OpenSelectedMedia();

    private void OnOpenMediaClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: MediaRow row })
        {
            SelectSingleRow(row);
            OpenSelectedMedia();
        }
    }

    private void OnSelectModeClick(object sender, RoutedEventArgs e)
    {
        _isSelectionMode = !_isSelectionMode;
        ClearSelectedRows();

        UpdateSelectionUi();
    }

    private void OnInspectorCopyClick(object sender, RoutedEventArgs e) =>
        OnImportSelectedClick(sender, e);

    private void OnNavigationMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        NavigationColumn.Width = new GridLength(240);
        ExpandedBrandText.Visibility = Visibility.Visible;
    }

    private void OnNavigationMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        NavigationColumn.Width = new GridLength(240);
        ExpandedBrandText.Visibility = Visibility.Visible;
    }

    private void OnAlbumCoverSizeChanged(object sender, SizeChangedEventArgs e) =>
        SetAspectHeight(sender, 1.5);

    private void OnMediaTileSizeChanged(object sender, SizeChangedEventArgs e) =>
        SetAspectHeight(sender, 1.18);

    private static void SetAspectHeight(object sender, double widthToHeightRatio)
    {
        if (sender is not FrameworkElement element || element.ActualWidth <= 0)
        {
            return;
        }

        var targetHeight = Math.Round(element.ActualWidth / widthToHeightRatio);
        if (Math.Abs(element.Height - targetHeight) > 0.5)
        {
            element.Height = targetHeight;
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string page })
        {
            ShowPage(page);
        }
    }

    private void OnBrowseDevicePhotosClick(object sender, RoutedEventArgs e) => ShowPage("Gallery");

    private void SetManualConnectionOpen(bool isOpen)
    {
        ManualConnectionModal.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnEnterIpClick(object sender, RoutedEventArgs e)
    {
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var payload = PairingQrPayloadCodec.Create(_desktopId, Environment.MachineName, code);
        var dialog = new PairDeviceWindow(
            payload,
            code,
            _language == UiLanguage.Chinese) { Owner = this };
        using var resolutionCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var resolutionTask = ResolvePairingWindowAsync(dialog, resolutionCancellation.Token);
        dialog.ShowDialog();
        resolutionCancellation.Cancel();
        try
        {
            await resolutionTask;
        }
        catch (OperationCanceledException)
        {
        }

        if (dialog.ResolvedDevice is not null)
        {
            var address = dialog.ResolvedDevice.Addresses
                .OrderByDescending(candidate => candidate.Source == DeviceAddressSource.Udp)
                .ThenByDescending(candidate => candidate.Source == DeviceAddressSource.Subnet)
                .First();
            _pendingPairingCode = dialog.ActiveCode;
            AddressTextBox.Text = $"{address.Host}:{address.Port}";
            StatusText.Text = L(
                $"Found {dialog.ResolvedDevice.DisplayName}; pairing…",
                $"已找到 {dialog.ResolvedDevice.DisplayName}，正在配对…");
            OnConnectClick(ConnectButton, new RoutedEventArgs());
            return;
        }

        if (dialog.ManualIpRequested)
        {
            SetManualConnectionOpen(true);
            StatusText.Text = L(
                "Enter the phone API address, then press Enter to connect.",
                "输入手机 API 地址，然后按 Enter 连接。");
            AddressTextBox.Focus();
        }
    }

    private async Task ResolvePairingWindowAsync(
        PairDeviceWindow dialog,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !dialog.IsClosed)
        {
            var activeCode = dialog.ActiveCode;
            var device = await _localDeviceDiscovery.ResolvePairingCodeAsync(
                _desktopId,
                activeCode,
                cancellationToken);
            if (device is not null && !dialog.IsClosed && dialog.ActiveCode == activeCode)
            {
                dialog.Complete(device, activeCode);
                return;
            }

            if (!dialog.IsClosed)
            {
                dialog.SetWaitingStatus(L(
                    "Waiting for the phone on Wi-Fi, hotspot or USB network…",
                    "正在 Wi-Fi、热点或 USB 网络中等待手机…"));
            }
            await Task.Delay(350, cancellationToken);
        }
    }

    private void OnCancelManualConnectionClick(object sender, RoutedEventArgs e) =>
        SetManualConnectionOpen(false);

    private async void OnFindDevicesClick(object sender, RoutedEventArgs e)
    {
        FindDevicesButton.IsEnabled = false;
        StatusText.Text = L("Searching the local network…", "正在搜索局域网设备…");
        try
        {
            var discovered = await _localDeviceDiscovery.DiscoverAsync(
                _desktopId,
                CancellationToken.None);
            foreach (var device in discovered)
            {
                _discoveryManager.Merge(device);
            }
            var selected = _discoveryManager.Devices
                .SelectMany(device => device.Addresses.Select(address => (device, address)))
                .OrderByDescending(candidate => candidate.device.PairingAvailable)
                .ThenByDescending(candidate => candidate.address.Source == DeviceAddressSource.Udp)
                .FirstOrDefault();
            if (selected.device is null)
            {
                StatusText.Text = L(
                    "No LinkGallery device was found. You can still enter an IP address.",
                    "未发现 LinkGallery 设备，你仍可手动输入 IP 地址。");
                ShowToast(L("No devices found", "未发现设备"));
                return;
            }

            AddressTextBox.Text = $"{selected.address.Host}:{selected.address.Port}";
            StatusText.Text = L(
                $"Found {selected.device.DisplayName}; connecting…",
                $"已发现 {selected.device.DisplayName}，正在连接…");
            OnConnectClick(ConnectButton, new RoutedEventArgs());
        }
        catch (Exception exception)
        {
            StatusText.Text = L(
                $"Discovery failed: {exception.Message}",
                $"设备发现失败：{exception.Message}");
            ShowToast(L("Device discovery failed", "设备发现失败"));
        }
        finally
        {
            FindDevicesButton.IsEnabled = true;
        }
    }

    private void OnAlbumCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlbumRow album })
        {
            OpenAlbum(album);
        }
    }

    private void OnSidebarAlbumClick(object sender, RoutedEventArgs e)
    {
        AlbumRow? album = sender switch
        {
            FrameworkElement { DataContext: AlbumRow row } => row,
            System.Windows.Controls.Button { Tag: string kind } => SmartAlbumRows.FirstOrDefault(
                candidate => string.Equals(candidate.Kind, kind, StringComparison.Ordinal)),
            _ => null,
        };

        if (album is null)
        {
            ShowPage("Albums");
            return;
        }

        OpenAlbum(album);
    }

    private void OnBackToAlbumsClick(object sender, RoutedEventArgs e) => ShowPage("Albums");

    private async void OnAlbumFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string filter })
        {
            _activeAlbumFilter = filter;
            await RefreshAlbumDetailRowsAsync(CancellationToken.None);
            UpdatePageSubtitle("AlbumDetail");
            ShowToast(L($"Showing {filter.ToLowerInvariant()}", $"正在显示{FilterLabel(filter)}"));
        }
    }

    private string FilterLabel(string filter) => filter switch
    {
        "Photos" => L("photos", "照片"),
        "Videos" => L("videos", "视频"),
        _ => L("all", "全部"),
    };

    private void OnNewAlbumClick(object sender, RoutedEventArgs e)
    {
        ShowToast(L(
            "My Albums is planned in issue #106",
            "“我的相册”已在 issue #106 中规划"));
    }

    private void OnCancelAlbumClick(object sender, RoutedEventArgs e) =>
        AlbumModal.Visibility = Visibility.Collapsed;

    private void OnCreateAlbumClick(object sender, RoutedEventArgs e)
    {
        AlbumModal.Visibility = Visibility.Collapsed;
        ShowToast(L(
            "My Albums is planned in issue #106",
            "“我的相册”已在 issue #106 中规划"));
    }

    private async void OnCopyAlbumClick(object sender, RoutedEventArgs e)
    {
        if (_activeAlbum is null)
        {
            ShowToast(L("No album selected", "未选择相册"));
            return;
        }

        var items = AlbumDetailRows.Select(static row => row.Item).ToArray();
        await CopyMediaAsync(items, CopyAlbumButton, "Copying album...");
    }

    private void OnAlbumDetailDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetSelectedRows().FirstOrDefault() is MediaRow row)
        {
            OpenMediaViewer(row);
        }
    }

    private async void OpenAlbum(AlbumRow album)
    {
        if (album.Kind == "SmartFavorites")
        {
            ShowToast(L(
                "Favorites is planned in issue #105",
                "收藏功能已在 issue #105 中规划"));
            return;
        }
        _activeAlbum = album;
        _activeAlbumFilter = "All";
        ShowPage("AlbumDetail");
        await RefreshAlbumDetailRowsAsync(CancellationToken.None);
    }

    private async Task RefreshAlbumDetailRowsAsync(CancellationToken cancellationToken)
    {
        AlbumDetailRows.Clear();
        AlbumDetailGroups.Clear();
        if (_activeAlbum is null)
        {
            AlbumDetailEmptyText.Visibility = Visibility.Visible;
            AlbumDetailList.Visibility = Visibility.Collapsed;
            return;
        }

        var selectedTypes = _activeAlbumFilter switch
        {
            "Photos" => new HashSet<MediaType> { MediaType.Image },
            "Videos" => new HashSet<MediaType> { MediaType.Video },
            _ => null,
        };
        var indexedItems = await _mediaIndex.SearchAsync(
            new MediaIndexQuery(
                DeviceId: _activeDeviceId,
                SearchText: null,
                Types: selectedTypes,
                FromInclusive: null,
                ToExclusive: null,
                Limit: 500,
                Offset: 0,
                AlbumId: _activeAlbum.Kind == "Device" ? _activeAlbum.AlbumId : null),
            cancellationToken);
        var rows = indexedItems
            .Where(item => IsInAlbum(item, _activeAlbum))
            .Select(item => new MediaRow(
                item,
                dateHeader: null,
                IsChinese
                    ? item.TakenAt.LocalDateTime.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)
                    : item.TakenAt.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)));

        foreach (var row in rows)
        {
            AlbumDetailRows.Add(row);
        }

        RefreshAlbumDetailGroups();
        AlbumFilterAllButton.Style = _activeAlbumFilter == "All"
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
        AlbumFilterPhotosButton.Style = _activeAlbumFilter == "Photos"
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");
        AlbumFilterVideosButton.Style = _activeAlbumFilter == "Videos"
            ? (Style)FindResource("LgSegmentButtonActive")
            : (Style)FindResource("LgSegmentButton");

        AlbumDetailEmptyText.Visibility = AlbumDetailRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumDetailList.Visibility = AlbumDetailRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsInAlbum(MediaItem item, AlbumRow album) =>
        album.Kind switch
        {
            "SmartFavorites" => false,
            "SmartVideos" => item.Type == MediaType.Video,
            "SmartScreenshots" => MatchesText(item, "screenshot"),
            "SmartRecent" => item.TakenAt >= DateTimeOffset.Now.AddDays(-30),
            _ when !string.IsNullOrWhiteSpace(album.AlbumId) =>
                string.Equals(item.AlbumId, album.AlbumId, StringComparison.Ordinal),
            _ => string.Equals(
                    string.IsNullOrWhiteSpace(item.AlbumName) ? "Unsorted" : item.AlbumName,
                    album.Name,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(album.RelativePath) ||
                 string.Equals(item.RelativePath, album.RelativePath, StringComparison.OrdinalIgnoreCase)),
        };

    private void ShowPage(string page)
    {
        var previousPage = _currentPage;
        _currentPage = page;
        GalleryPage.Visibility = page == "Gallery" ? Visibility.Visible : Visibility.Collapsed;
        AlbumsPage.Visibility = page == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        AlbumDetailPage.Visibility = page == "AlbumDetail" ? Visibility.Visible : Visibility.Collapsed;
        DevicePage.Visibility = page == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page is not ("Gallery" or "AlbumDetail"))
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
        }

        BackToAlbumsButton.Visibility = page == "AlbumDetail" ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = page switch
        {
            "Gallery" => L("Photos", "照片"),
            "Albums" => L("Albums", "相册"),
            "AlbumDetail" => _activeAlbum?.Name ?? L("Album", "相册"),
            "Devices" => L("Devices", "设备"),
            "Settings" => L("Settings", "设置"),
            _ => page,
        };
        UpdatePageSubtitle(page);
        SearchChrome.Visibility = page is "Gallery" or "Albums" ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.Visibility = page is "Gallery" or "Albums" ? Visibility.Visible : Visibility.Collapsed;
        FilterPanel.Visibility = page == "Gallery" ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholderText.Text = page == "Gallery"
            ? L("Search media", "搜索媒体")
            : L("Search albums", "搜索相册");
        NewAlbumButton.Visibility = page == "Albums" ? Visibility.Visible : Visibility.Collapsed;
        EnterIpButton.Visibility = page == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        FindDevicesButton.Visibility = page == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        CopyAlbumButton.Visibility = page == "AlbumDetail" ? Visibility.Visible : Visibility.Collapsed;
        PageActionBar.Visibility = page is "Gallery" or "AlbumDetail" ? Visibility.Visible : Visibility.Collapsed;
        GridViewButton.Visibility = Visibility.Collapsed;
        if (page != "Devices")
        {
            SetManualConnectionOpen(false);
        }

        SetActiveNavigation(page);
        if (page is not ("Gallery" or "AlbumDetail") || previousPage != page)
        {
            ResetSelectionMode();
        }
        else
        {
            UpdateSelectionUi();
        }
    }

    private void UpdatePageSubtitle(string page)
    {
        switch (page)
        {
            case "Albums":
                var albumCount = SmartAlbumRows.Count + DeviceAlbumRows.Count + MyAlbumRows.Count;
                StatusText.Text = albumCount == 1
                    ? L("1 album", "1 个相册")
                    : L($"{albumCount:N0} albums", $"{albumCount:N0} 个相册");
                break;
            case "AlbumDetail":
                StatusText.Text = _activeAlbum is null
                    ? L("0 items", "0 项")
                    : L(
                        $"{AlbumDetailRows.Count:N0} items · {AlbumKindLabel(_activeAlbum)}",
                        $"{AlbumDetailRows.Count:N0} 项 · {AlbumKindLabel(_activeAlbum)}");
                break;
            case "Devices":
                StatusText.Text = _source is { IsOffline: false }
                    ? L("1 connected source", "1 个已连接来源")
                    : L("No connected source", "没有已连接来源");
                break;
            case "Settings":
                StatusText.Text = L("Desktop preferences", "桌面端偏好设置");
                break;
        }
    }

    private string AlbumKindLabel(AlbumRow album) =>
        album.Kind.StartsWith("Smart", StringComparison.Ordinal)
            ? L("Smart album", "智能相册")
            : album.Kind == "My" ? L("My album", "我的相册") : L("Device album", "设备相册");

    private void SetActiveNavigation(string page)
    {
        SetNavigationButtonState(NavGalleryButton, page == "Gallery");
        SetNavigationButtonState(NavAlbumsButton, page is "Albums" or "AlbumDetail");
        SetNavigationButtonState(NavDevicesButton, page == "Devices");
        SetNavigationButtonState(NavSettingsButton, page == "Settings");
    }

    private static void SetNavigationButtonState(System.Windows.Controls.Button button, bool active)
    {
        button.Background = active ? new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xFF)) : Brushes.Transparent;
        button.Foreground = active ? new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xE3)) : new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
        button.BorderBrush = Brushes.Transparent;
    }

    private void ShowToast(string message)
    {
        _toastTimer.Stop();
        ToastText.Text = message;
        ToastHost.Opacity = 1;
        if (ToastHost.RenderTransform is TranslateTransform transform)
        {
            transform.Y = 0;
        }

        _toastTimer.Start();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        ToastHost.Opacity = 0;
        if (ToastHost.RenderTransform is TranslateTransform transform)
        {
            transform.Y = 16;
        }
    }

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionUi();
    }

    private void OnMediaTilePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaRow row })
        {
            return;
        }

        if (_isSelectionMode)
        {
            row.IsSelected = !row.IsSelected;

            UpdateSelectionUi();
            e.Handled = true;
            return;
        }

        SelectSingleRow(row);
        if (sender is FrameworkElement element)
        {
            element.Focus();
            Keyboard.Focus(element);
        }

        UpdateSelectionUi();
        e.Handled = true;
    }

    private void OnMediaTileKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) ||
            sender is not FrameworkElement { DataContext: MediaRow row })
        {
            return;
        }

        SelectSingleRow(row);
        UpdateSelectionUi();
        OpenMediaViewer(row);
        e.Handled = true;
    }

    private MediaRow[] GetSelectedRows()
    {
        var rows = _currentPage == "AlbumDetail" ? AlbumDetailRows : TimelineRows;
        return rows.Where(static row => row.IsSelected).ToArray();
    }

    private void ResetSelectionMode()
    {
        _isSelectionMode = false;
        ClearSelectedRows();
        UpdateSelectionUi();
    }

    private void SelectSingleRow(MediaRow row)
    {
        ClearSelectedRows();
        row.IsSelected = true;
    }

    private void ClearSelectedRows()
    {
        foreach (var row in TimelineRows)
        {
            row.IsSelected = false;
        }

        foreach (var row in AlbumDetailRows)
        {
            row.IsSelected = false;
        }
    }

    private void UpdateSelectionUi()
    {
        var selectedRows = GetSelectedRows();
        var count = selectedRows.Length;
        ImportSelectedButton.Content = _isSelectionMode ? L("Done", "完成") : L("Multi-select", "多选");
        SelectionSummaryText.Text = count == 1
            ? L("1 selected", "已选择 1 项")
            : L($"{count:N0} selected", $"已选择 {count:N0} 项");
        SelectionSummaryText.Visibility = _isSelectionMode && count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InspectorCopyButton.Content = count > 0
            ? (count == 1
                ? L("Copy selected", "复制所选")
                : L($"Copy {count:N0} selected", $"复制已选择的 {count:N0} 项"))
            : L("Copy to computer", "复制到电脑");
        InspectorCopyButton.IsEnabled = count > 0;
        if (_isSelectionMode && count > 1)
        {
            UpdateInspectorForSelection(count);
        }
        else
        {
            UpdateInspector(selectedRows.FirstOrDefault());
        }
    }

    private void UpdateInspector(MediaRow? row)
    {
        if (row is null)
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorPreviewBorder.Visibility = Visibility.Collapsed;
            InspectorPreviewImage.Source = null;
            InspectorPreviewImage.Visibility = Visibility.Collapsed;
            InspectorTypeText.Text = "-";
            InspectorSizeText.Text = "-";
            InspectorResolutionText.Text = "-";
            InspectorDeviceText.Text = "-";
            return;
        }

        var item = row.Item;
        InspectorPanel.Visibility = Visibility.Visible;
        InspectorPreviewBorder.Visibility = row.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        InspectorPreviewImage.Source = row.Thumbnail;
        InspectorPreviewImage.Visibility = row.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        InspectorTitleText.Text = item.FileName;
        InspectorMetaText.Text = item.TakenAt.LocalDateTime.ToString("d MMMM yyyy · HH:mm", CultureInfo.InvariantCulture);
        InspectorTypeText.Text = FormatMediaKind(item);
        InspectorSizeText.Text = FormatSize(item.FileSize);
        InspectorResolutionText.Text = item.Width.HasValue && item.Height.HasValue
            ? $"{item.Width.Value} × {item.Height.Value}"
            : "-";
        InspectorDeviceText.Text = string.IsNullOrWhiteSpace(item.SourceDevice)
            ? item.DeviceId
            : item.SourceDevice;
    }

    private void UpdateInspectorForSelection(int count)
    {
        InspectorPanel.Visibility = Visibility.Visible;
        InspectorPreviewBorder.Visibility = Visibility.Collapsed;
        InspectorPreviewImage.Source = null;
        InspectorPreviewImage.Visibility = Visibility.Collapsed;
        InspectorTitleText.Text = L($"{count:N0} selected", $"已选择 {count:N0} 项");
        InspectorMetaText.Text = L("Ready to copy selected media", "可以复制所选媒体");
        InspectorTypeText.Text = L("Mixed", "多种类型");
        InspectorSizeText.Text = "-";
        InspectorResolutionText.Text = "-";
        InspectorDeviceText.Text = "-";
    }

    private void OnCloseInspectorClick(object sender, RoutedEventArgs e)
    {
        InspectorPanel.Visibility = Visibility.Collapsed;
        if (_isSelectionMode)
        {
            return;
        }

        ClearSelectedRows();
    }

    private static string FormatMediaKind(MediaItem item)
    {
        var extension = Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = item.Type == MediaType.Video ? "Video" : "Photo";
        }

        return item.Type == MediaType.Video
            ? $"{extension} video"
            : $"{extension} photo";
    }

    private void OnTimelineKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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
        var selected = GetSelectedRows().FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        OpenMediaViewer(selected);
    }

    private void OpenMediaViewer(MediaRow row)
    {
        _backgroundIndexGate.Pause("viewer");
        _viewerRow = row;
        _viewerZoom = 1;
        ViewerPhotoScale.ScaleX = 1;
        ViewerPhotoScale.ScaleY = 1;
        ViewerPhoto.Background = Brushes.Black;
        ViewerImage.Source = row.Thumbnail;
        ViewerImage.Visibility = row.Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        ViewerNameText.Text = row.Item.FileName;
        ViewerMetaText.Text = row.Item.TakenAt.LocalDateTime.ToString("d MMMM yyyy · HH:mm", CultureInfo.InvariantCulture);
        ViewerOverlay.Visibility = Visibility.Visible;
    }

    private void OnCloseViewerClick(object sender, RoutedEventArgs e)
    {
        _backgroundIndexGate.Resume("viewer");
        ViewerOverlay.Visibility = Visibility.Collapsed;
        _viewerRow = null;
        _viewerZoom = 1;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        _viewerZoom = Math.Min(2.2, _viewerZoom + 0.18);
        ViewerPhotoScale.ScaleX = _viewerZoom;
        ViewerPhotoScale.ScaleY = _viewerZoom;
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        _viewerZoom = Math.Max(0.72, _viewerZoom - 0.18);
        ViewerPhotoScale.ScaleX = _viewerZoom;
        ViewerPhotoScale.ScaleY = _viewerZoom;
    }

    private async void OnViewerCopyClick(object sender, RoutedEventArgs e)
    {
        if (_viewerRow is null)
        {
            return;
        }

        await CopyMediaAsync([_viewerRow.Item], sender as System.Windows.Controls.Button, L("Copying...", "正在复制…"));
    }

    private void OnViewerPhotoSizeChanged(object sender, SizeChangedEventArgs e) =>
        SetAspectHeight(sender, 1.45);

    private async void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 ||
            e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - (e.ViewportHeight * 1.5))
        {
            return;
        }

        try
        {
            var cancellationToken = _connectionCancellation?.Token ?? CancellationToken.None;
            if (_source is not null &&
                !_source.IsOffline &&
                string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                await LoadRemoteNextPageAsync(_source, cancellationToken);
                return;
            }

            if (!_hasMoreIndexedItems)
            {
                UpdateTimelineFooter();
                return;
            }

            await LoadIndexedPageAsync(
                reset: false,
                L("No media available in local index", "本地索引中没有可显示的媒体"),
                cancellationToken);
            UpdateIndexedStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = L($"Cannot continue loading: {exception.Message}", $"无法继续加载：{exception.Message}");
        }
    }

    private async Task LoadRemoteNextPageAsync(
        CachingReadOnlyMediaSource source,
        CancellationToken cancellationToken)
    {
        if (_isLoadingPage || !_hasMoreRemoteItems || _remoteNextCursor is null)
        {
            UpdateTimelineFooter();
            return;
        }

        _isLoadingPage = true;
        SetTimelineFooter(L("Loading more...", "正在加载更多…"));
        try
        {
            var page = await source.GetMediaPageAsync(
                new MediaQuery(Cursor: _remoteNextCursor, Limit: PageSize),
                cancellationToken);
            AppendTimelineItems(DeduplicateRemoteItems(page.Items));
            _remoteNextCursor = page.NextCursor;
            _hasMoreRemoteItems = page.HasMore || page.NextCursor is not null;
            TimelineScrollViewer.Visibility = TimelineRows.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmptyText.Visibility = TimelineRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusText.Text = page.Total.HasValue
                ? L(
                    $"Connected · Showing {TimelineRows.Count:N0}/{page.Total.Value:N0} items",
                    $"已连接 · 已显示 {TimelineRows.Count:N0}/{page.Total.Value:N0} 项")
                : L(
                    $"Connected · Showing {TimelineRows.Count:N0} items",
                    $"已连接 · 已显示 {TimelineRows.Count:N0} 项");
            UpdateTimelineFooter();
        }
        catch
        {
            SetTimelineFooter(L("Load failed", "加载失败"));
            throw;
        }
        finally
        {
            _isLoadingPage = false;
        }
    }

    private void UpdateTimelineFooter()
    {
        if (_source is not null && !_source.IsOffline && TimelineRows.Count > 0)
        {
            SetTimelineFooter(_hasMoreRemoteItems ? null : L("No more media", "没有更多媒体"));
        }
        else if (_source is null && TimelineRows.Count > 0)
        {
            SetTimelineFooter(_hasMoreIndexedItems ? null : L("No more cached media", "没有更多缓存媒体"));
        }
        else
        {
            SetTimelineFooter(null);
        }
    }

    private void SetTimelineFooter(string? message)
    {
        TimelineFooterText.Text = message ?? "";
        TimelineFooterText.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            _queryCancellation = new CancellationTokenSource();
            await LoadIndexedPageAsync(
                reset: true,
                L("No matching media in local index", "本地索引中没有匹配的媒体"),
                _queryCancellation.Token);
            UpdateIndexedStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = L($"Cannot read local index: {exception.Message}", $"无法读取本地索引：{exception.Message}");
        }
    }

    private async void OnSearchFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _currentPage != "Gallery") return;
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = new CancellationTokenSource();
        try
        {
            await LoadIndexedPageAsync(
                reset: true,
                L("No matching media in local index", "本地索引中没有匹配的媒体"),
                _queryCancellation.Token);
            UpdateIndexedStatus();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_source is not null)
            {
                await _source.ClearThumbnailCacheAsync();
            }
            else if (Directory.Exists(_thumbnailCacheDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(_thumbnailCacheDirectory, "*.jpg", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(path);
                }
            }

            foreach (var row in TimelineRows)
            {
                row.Thumbnail = null;
            }

            _decodedThumbnails.Clear();
            _decodedThumbnailOrder.Clear();
            UpdateSettingsSummary();
            StatusText.Text = L("Thumbnail cache cleared", "缩略图缓存已清理");
        }
        catch (IOException exception)
        {
            StatusText.Text = L($"Cache cleanup failed: {exception.Message}", $"缓存清理失败：{exception.Message}");
        }
    }

    private void OnChooseDownloadFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = L("Choose download folder", "选择下载文件夹"),
            Multiselect = false,
            InitialDirectory = Directory.Exists(_downloadDirectory)
                ? _downloadDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        _downloadDirectory = picker.FolderName;
        UpdateSettingsSummary();
    }

    private void OnFolderSwitchClick(object sender, MouseButtonEventArgs e)
    {
        _preserveAlbumFolders = !_preserveAlbumFolders;
        UpdateSwitchVisuals();
        ShowToast(_preserveAlbumFolders
            ? L("Album folders preserved", "已保留相册文件夹")
            : L("Album folders off", "已关闭相册文件夹"));
    }

    private void OnMotionSwitchClick(object sender, MouseButtonEventArgs e)
    {
        _reduceMotion = !_reduceMotion;
        UpdateSwitchVisuals();
        ShowToast(_reduceMotion ? L("Reduced motion on", "已开启减少动画") : L("Reduced motion off", "已关闭减少动画"));
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag })
        {
            return;
        }

        _language = string.Equals(tag, "zh", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Chinese
            : UiLanguage.English;
        RefreshAlbumRows();
        ApplyLanguage();
        SavePreferences();
        ShowToast(L("Language set to English", "语言已切换为中文"));
    }

    private void OnCloseBehaviorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag })
        {
            return;
        }

        _closeBehavior = tag switch
        {
            "hide" => CloseBehavior.HideToTray,
            "quit" => CloseBehavior.QuitApp,
            _ => CloseBehavior.AskEveryTime,
        };
        SavePreferences();
        UpdateCloseBehaviorButtonStyles();
        ShowToast(_closeBehavior switch
        {
            CloseBehavior.HideToTray => L("Close button hides to tray", "关闭按钮将隐藏到托盘"),
            CloseBehavior.QuitApp => L("Close button quits LinkGallery", "关闭按钮将退出 LinkGallery"),
            _ => L("Close button will ask every time", "关闭按钮将每次询问"),
        });
    }

    private async void OnImportSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedRows()
            .Select(static row => row.Item)
            .ToArray();
        await CopyMediaAsync(
            selected,
            sender as System.Windows.Controls.Button,
            selected.Length == 1
                ? L("Copying...", "正在复制…")
                : L($"Copying {selected.Length:N0} items...", $"正在复制 {selected.Length:N0} 项…"));
    }

    private async Task CopyMediaAsync(MediaItem[] selected, System.Windows.Controls.Button? sourceButton, string workingText)
    {
        if (selected.Length == 0)
        {
            ShowToast(L("Select media first", "请先选择媒体"));
            return;
        }

        if (_source is null || _source.IsOffline)
        {
            ShowToast(L("Connect the device first", "请先连接设备"));
            return;
        }

        string destinationDirectory;
        if (string.Equals(
                Environment.GetEnvironmentVariable("LINKGALLERY_E2E"),
                "1",
                StringComparison.Ordinal) &&
            Environment.GetEnvironmentVariable("LINKGALLERY_E2E_IMPORT_DIRECTORY")
                is { Length: > 0 } e2eImportDirectory)
        {
            destinationDirectory = Path.GetFullPath(e2eImportDirectory);
            Directory.CreateDirectory(destinationDirectory);
        }
        else
        {
            destinationDirectory = _downloadDirectory;
            Directory.CreateDirectory(destinationDirectory);
        }

        var originalContent = sourceButton?.Content;
        if (sourceButton is not null)
        {
            sourceButton.IsEnabled = false;
            sourceButton.Content = workingText;
        }

        try
        {
            foreach (var item in selected)
            {
                var itemDestination = _preserveAlbumFolders && !string.IsNullOrWhiteSpace(item.AlbumName)
                    ? Path.Combine(destinationDirectory, SanitizePathSegment(item.AlbumName))
                    : destinationDirectory;
                Directory.CreateDirectory(itemDestination);
                await _transferCoordinator.EnqueueAsync(item, itemDestination);
            }

            RefreshTransferRows();
            UpdateSettingsSummary();
            var message = selected.Length == 1
                ? L("Copy task created", "已创建复制任务")
                : L(
                    $"Copy task created for {selected.Length:N0} items",
                    $"已为 {selected.Length:N0} 项创建复制任务");
            StatusText.Text = message;
            ShowToast(message);
            ResetSelectionMode();
        }
        catch (Exception exception)
        {
            StatusText.Text = L($"Cannot add to copy queue: {exception.Message}", $"无法加入导入队列：{exception.Message}");
            ShowToast(L("Copy failed", "复制失败"));
        }
        finally
        {
            if (sourceButton is not null)
            {
                sourceButton.Content = originalContent;
                sourceButton.IsEnabled = true;
            }
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
            StatusText.Text = L($"Pause failed: {exception.Message}", $"暂停失败：{exception.Message}");
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
            StatusText.Text = L($"Resume failed: {exception.Message}", $"恢复失败：{exception.Message}");
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
            StatusText.Text = L($"Clear failed: {exception.Message}", $"清理失败：{exception.Message}");
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Guid jobId })
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
            StatusText.Text = L($"Retry failed: {exception.Message}", $"重试失败：{exception.Message}");
        }
    }

    private void OnTransferRefreshTick(object? sender, EventArgs e) => RefreshTransferRows();

    private void RefreshTransferRows()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = _transferCoordinator.GetJobs();
        var transferActive = jobs.Any(job =>
            !job.IsTerminal && job.Status != TransferStatus.Paused);
        if (transferActive && !_transferGatePaused)
        {
            _backgroundIndexGate.Pause("transfer");
            _transferGatePaused = true;
        }
        else if (!transferActive && _transferGatePaused)
        {
            _backgroundIndexGate.Resume("transfer");
            _transferGatePaused = false;
        }
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

        if (jobs.Count > 0)
        {
            var completed = jobs.Count(job => job.Status == TransferStatus.Completed);
            var failed = jobs.Count(job => job.Status == TransferStatus.Failed);
            var remainingBytes = jobs
                .Where(job => !job.IsTerminal)
                .Sum(job => job.TotalBytes - job.BytesTransferred);
            SyncStateText.Text = L(
                $"{completed:N0}/{jobs.Count:N0} copied · {failed:N0} failed · {FormatSize(remainingBytes)} left",
                $"已复制 {completed:N0}/{jobs.Count:N0} · 失败 {failed:N0} · 剩余 {FormatSize(remainingBytes)}");
            TransferStatusText.Text = TransferRows.FirstOrDefault(row => row.StatusText.Length > 0)?.StatusText
                ?? L("Completed", "已完成");
            ImportSummaryText.Text = $"{completed:N0}/{jobs.Count:N0} 瀹屾垚 · completed";
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        Disconnect(clearTimeline: true);
        try
        {
            await LoadIndexedPageAsync(
                reset: true,
                L("No media in local cache", "本地缓存中还没有媒体"),
                CancellationToken.None);
            UpdateIndexedStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = L($"Cannot read local index: {exception.Message}", $"无法读取本地索引：{exception.Message}");
        }
    }

    private async void OnForgetDeviceClick(object sender, RoutedEventArgs e)
    {
        var device = _activePairedDevice;
        var accessToken = _activeAccessToken;
        var apiAddress = _activeApiAddress;
        if (device is null)
        {
            return;
        }

        ForgetDeviceButton.IsEnabled = false;
        var remoteRevoked = false;
        if (apiAddress is not null && !string.IsNullOrWhiteSpace(accessToken))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _pairingClient.RevokeAsync(apiAddress, accessToken, timeout.Token);
                remoteRevoked = true;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
                // Local removal must remain available while the phone is offline.
            }
        }

        await _accessTokenStore.DeleteAsync(device.CredentialKey, CancellationToken.None);
        await _pairedDeviceStore.RemovePairedDeviceAsync(device.DeviceId, CancellationToken.None);
        Disconnect(clearTimeline: true);
        ShowToast(remoteRevoked
            ? L("Pairing revoked and device forgotten", "已撤销配对并忘记设备")
            : L("Device forgotten locally; the phone was unavailable", "已在本机忘记设备；手机当前不可用"));
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isQuitting)
        {
            Dispose();
            return;
        }

        switch (_closeBehavior)
        {
            case CloseBehavior.HideToTray:
                e.Cancel = true;
                HideToTray();
                break;
            case CloseBehavior.QuitApp:
                _isQuitting = true;
                _notifyIcon?.Dispose();
                _notifyIcon = null;
                Dispose();
                break;
            default:
                e.Cancel = true;
                ShowClosePrompt();
                break;
        }
    }

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
        _queryGate.Dispose();
        _httpClient.Dispose();
        _localCopies.Dispose();
        _mediaIndex.Dispose();
        _pairedDeviceStore.Dispose();
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Disconnect(bool clearTimeline)
    {
        _transferSourceResolver.Clear();
        _backgroundSyncCancellation?.Cancel();
        _backgroundSyncCancellation?.Dispose();
        _backgroundSyncCancellation = null;
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;
        _source?.Dispose();
        _source = null;
        _activeDeviceId = null;
        _activeAccessToken = null;
        _activePairedDevice = null;
        _activeApiAddress = null;
        _remoteNextCursor = null;
        _hasMoreRemoteItems = false;
        _hasMoreIndexedItems = false;
        _isLoadingPage = false;
        _loadedRemoteIds.Clear();
        _decodedThumbnails.Clear();
        _decodedThumbnailOrder.Clear();
        DevicePanel.Visibility = Visibility.Collapsed;
        DeviceCardsGrid.Visibility = Visibility.Collapsed;
        ConnectedDeviceCard.Visibility = Visibility.Collapsed;
        SetManualConnectionOpen(false);
        DevicesEmptyText.Visibility = Visibility.Visible;
        UpdateOnlineIndicators();
        SyncStateText.Text = L("Local cache", "本地缓存");
        TimelineScrollViewer.Visibility = Visibility.Collapsed;
        SetTimelineFooter(null);
        if (clearTimeline)
        {
            TimelineRows.Clear();
            TimelineGroups.Clear();
            RefreshAlbumRows();
        }

        EmptyText.Text = L("Enter a phone address to connect", "输入手机地址开始连接");
        EmptyText.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = false;
        ForgetDeviceButton.IsEnabled = false;
        SetLoading(false);
    }

    private void SetLoading(bool isLoading, string? status = null)
    {
        LoadingProgress.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading)
        {
            LoadingProgress.IsIndeterminate = true;
            LoadingProgress.Value = 0;
            DeviceStatusText.Text = L("Busy", "忙碌");
            NavDevicesOnlineDot.Visibility = Visibility.Collapsed;
            StatusOnlineDot.Visibility = Visibility.Collapsed;
        }

        ConnectButton.IsEnabled = !isLoading;
        AddressTextBox.IsEnabled = !isLoading;
        DisconnectButton.IsEnabled = isLoading || DevicePanel.Visibility == Visibility.Visible;
        ForgetDeviceButton.IsEnabled =
            !isLoading && DevicePanel.Visibility == Visibility.Visible && _activePairedDevice is not null;
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    private void UpdateIndexedStatus()
    {
        var mode = _source is null
            ? L("Offline cache", "离线缓存")
            : _source.IsOffline ? L("Offline cache", "离线缓存") : L("Online", "在线");
        var suffix = _hasMoreIndexedItems ? "" : L(" · fully loaded", " · 已全部加载");
        StatusText.Text = L(
            $"{mode} · Showing {TimelineRows.Count:N0} items{suffix}",
            $"{mode} · 已显示 {TimelineRows.Count:N0} 项{suffix}");
        UpdateOnlineIndicators();
        SyncStateText.Text = L($"{TimelineRows.Count:N0} shown", $"已显示 {TimelineRows.Count:N0} 项");
    }

    private void ShowConnectionError(Exception exception)
    {
        DisconnectButton.IsEnabled = false;
        StatusText.Text = exception switch
        {
            FormatException => exception.Message,
            MediaSourceTimeoutException =>
                L(
                    "Connection timed out. Make sure the phone is online, the address is correct, and both devices are on the same Wi-Fi.",
                    "连接超时。请确认手机在线、地址正确，且两台设备在同一 Wi-Fi。"),
            MediaSourceConnectionException
                { Failure: MediaSourceConnectionFailure.ConnectionRefused } =>
                L(
                    "Connection refused. The address is reachable but the phone service is not listening; keep the Android page in foreground and retry.",
                    "连接被拒绝。地址可以到达，但手机服务未监听；请保持 Android 页面在前台后重试。"),
            MediaSourceConnectionException
                { Failure: MediaSourceConnectionFailure.NetworkUnreachable } =>
                L(
                    "Network unreachable. For real phones check same Wi-Fi, Windows firewall, and AP isolation; for emulator use ADB forward and 127.0.0.1.",
                    "网络不可达。真机请检查同一 Wi-Fi、Windows 防火墙和 Wi-Fi AP 客户端隔离；模拟器请使用 ADB forward 和 127.0.0.1。"),
            MediaSourceConnectionException =>
                L(
                    "Cannot connect to phone. For real phones check IP, port and Wi-Fi; for emulator use ADB forward and 127.0.0.1.",
                    "无法连接手机。真机请检查 IP、端口和 Wi-Fi；模拟器请使用 ADB forward 和 127.0.0.1。"),
            MediaSourceProtocolException => L($"Protocol error: {exception.Message}", $"协议错误：{exception.Message}"),
            MediaSourceHttpException { StatusCode: HttpStatusCode.Forbidden } =>
                L(
                    "Phone has not granted photo and video permission. Grant it on Android and retry.",
                    "手机未授予照片和视频读取权限，请在手机端授权后重试。"),
            MediaSourceHttpException { StatusCode: HttpStatusCode.BadRequest } =>
                L($"Phone rejected the request: {exception.Message}", $"手机拒绝了请求：{exception.Message}"),
            MediaSourceHttpException => L($"Phone returned an error: {exception.Message}", $"手机返回错误：{exception.Message}"),
            HttpRequestException =>
                L(
                    "Cannot connect to phone. Check IP, port, Wi-Fi and phone service; local index remains available.",
                    "无法连接手机。请检查 IP、端口、Wi-Fi 和手机服务状态；仍可浏览本地索引。"),
            _ => L($"Connection failed: {exception.Message}", $"连接失败：{exception.Message}"),
        };
    }

    private readonly record struct DecodedThumbnailKey(
        string DeviceId,
        string RemoteId,
        long Version,
        int Width,
        int Height)
    {
        public static DecodedThumbnailKey Create(MediaItem item, ThumbnailSize size) =>
            new(
                item.DeviceId,
                item.RemoteId,
                item.Generation ?? item.ModifiedAt.ToUnixTimeMilliseconds(),
                size.Width,
                size.Height);
    }

    public sealed class MediaRow : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;
        private bool _isSelected;
        private double _aspectRatio;

        public MediaRow(MediaItem item, string? dateHeader, string dateGroup)
        {
            Item = item;
            DateHeader = dateHeader;
            DateGroup = dateGroup;
            PlaceholderBrush = CreatePlaceholderBrush($"{item.DeviceId}:{item.RemoteId}:{item.FileName}");
            TypeLabel = item.Type == MediaType.Image ? "图片" : "视频";
            FileName = item.FileName;
            Details = FormatDetails(item);
            TakenAt = item.TakenAt.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
            AspectRatio = CalculateAspectRatio(item);
            IsVideo = item.Type == MediaType.Video;
            DurationText = item.DurationMilliseconds.HasValue
                ? FormatDuration(item.DurationMilliseconds.Value)
                : string.Empty;
            AlbumName = item.AlbumName ?? "未分类";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MediaItem Item { get; }

        public string? DateHeader { get; }

        public string DateGroup { get; }

        public SolidColorBrush PlaceholderBrush { get; }

        public string TypeLabel { get; }

        public string FileName { get; }

        public string RemoteId => Item.RemoteId;

        public string Details { get; }

        public string TakenAt { get; }

        public string AlbumName { get; }

        public double AspectRatio
        {
            get => _aspectRatio;
            private set
            {
                var ratio = Math.Clamp(value, 0.35, 3.5);
                if (Math.Abs(_aspectRatio - ratio) < 0.001)
                {
                    return;
                }

                _aspectRatio = ratio;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AspectRatio)));
            }
        }

        public bool IsVideo { get; }

        public string DurationText { get; }

        public bool IsThumbnailLoading { get; set; }

        public CancellationTokenSource? ThumbnailLoadCancellation { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

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
                if (value is { Width: > 0, Height: > 0 })
                {
                    AspectRatio = value.Width / value.Height;
                }

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

        private static double CalculateAspectRatio(MediaItem item)
        {
            if (item.Width is > 0 && item.Height is > 0)
            {
                return Math.Clamp(item.Width.Value / (double)item.Height.Value, 0.35, 3.5);
            }

            return 1;
        }

        private static string FormatDuration(long durationMilliseconds)
        {
            var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
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

        private static SolidColorBrush CreatePlaceholderBrush(string key)
        {
            Color[] palette =
            [
                Color.FromRgb(0xB7, 0xC9, 0xD6),
                Color.FromRgb(0xD8, 0xB7, 0xA5),
                Color.FromRgb(0xA4, 0xBC, 0xA5),
                Color.FromRgb(0xC5, 0xB2, 0xD9),
                Color.FromRgb(0xDB, 0xC9, 0x87),
                Color.FromRgb(0x9D, 0xBB, 0xCA),
                Color.FromRgb(0xC6, 0xA5, 0xA5),
                Color.FromRgb(0xAE, 0xC2, 0xD4),
            ];

            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(key);
            var brush = new SolidColorBrush(palette[Math.Abs(hash % palette.Length)]);
            brush.Freeze();
            return brush;
        }
    }

    public sealed class MediaGroupRow
    {
        public MediaGroupRow(string date, IEnumerable<MediaRow> rows)
        {
            Date = date;
            Rows = new ObservableCollection<MediaRow>(rows);
        }

        public string Date { get; }

        public ObservableCollection<MediaRow> Rows { get; }
    }

    public sealed class AlbumRow(
        string name,
        int count,
        int imageCount,
        int videoCount,
        System.Windows.Media.Brush? coverBrush = null,
        string kind = "Device",
        string? countText = null,
        string? typeSummary = null,
        string? albumId = null,
        string? relativePath = null)
    {
        public string Name { get; } = name;

        public string CountText { get; } = countText ?? (count == 1 ? "1 item" : $"{count:N0} items");

        public string TypeSummary { get; } = typeSummary ?? $"{imageCount:N0} photos · {videoCount:N0} videos";

        public System.Windows.Media.Brush CoverBrush { get; } =
            coverBrush ?? new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xFF));

        public string Kind { get; } = kind;

        public string? AlbumId { get; } = albumId;

        public string? RelativePath { get; } = relativePath;
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

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Album" : sanitized;
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

public sealed class JustifiedGalleryPanel : System.Windows.Controls.Panel
{
    private const double TargetRowHeight = 168;
    private const double MinimumRowHeight = 96;
    private const double MaximumSparseRowHeight = 280;
    private const double ItemGap = 6;
    private const double FallbackWidth = 960;

    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.RegisterAttached(
        "AspectRatio",
        typeof(double),
        typeof(JustifiedGalleryPanel),
        new FrameworkPropertyMetadata(
            1d,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure |
            FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static void SetAspectRatio(UIElement element, double value) =>
        element.SetValue(AspectRatioProperty, value);

    public static double GetAspectRatio(UIElement element) =>
        element.GetValue(AspectRatioProperty) is double ratio
            ? Math.Clamp(ratio, 0.35, 3.5)
            : 1;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        var width = ResolveWidth(availableSize.Width);
        var rows = BuildRows(width);
        foreach (var row in rows)
        {
            for (var index = row.StartIndex; index < row.EndIndex; index++)
            {
                var child = InternalChildren[index];
                child.Measure(new System.Windows.Size(GetAspectRatio(child) * row.Height, row.Height));
            }
        }

        return new System.Windows.Size(width, rows.Count == 0 ? 0 : rows[^1].Bottom);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var width = ResolveWidth(finalSize.Width);
        var rows = BuildRows(width);
        foreach (var row in rows)
        {
            var x = 0d;
            for (var index = row.StartIndex; index < row.EndIndex; index++)
            {
                var child = InternalChildren[index];
                var childWidth = index == row.EndIndex - 1 && row.StretchToWidth
                    ? Math.Max(0, width - x)
                    : GetAspectRatio(child) * row.Height;
                child.Arrange(new Rect(x, row.Top, childWidth, row.Height));
                x += childWidth + ItemGap;
            }
        }

        return finalSize;
    }

    private List<GalleryRow> BuildRows(double width)
    {
        var rows = new List<GalleryRow>();
        if (InternalChildren.Count == 0 || width <= 0)
        {
            return rows;
        }

        var start = 0;
        var ratioSum = 0d;
        var y = 0d;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            ratioSum += GetAspectRatio(InternalChildren[index]);
            var count = index - start + 1;
            var filledWidthAtTarget = ratioSum * TargetRowHeight + Math.Max(0, count - 1) * ItemGap;
            var shouldCloseRow = filledWidthAtTarget >= width || index == InternalChildren.Count - 1;
            if (!shouldCloseRow)
            {
                continue;
            }

            var isLastRow = index == InternalChildren.Count - 1;
            var gaps = Math.Max(0, count - 1) * ItemGap;
            var justifiedHeight = (width - gaps) / Math.Max(0.1, ratioSum);
            var stretch = !isLastRow || count > 1;
            var height = isLastRow
                ? count == 1
                    ? TargetRowHeight
                    : Math.Clamp(justifiedHeight, MinimumRowHeight, MaximumSparseRowHeight)
                : Math.Max(MinimumRowHeight, justifiedHeight);
            rows.Add(new GalleryRow(start, index + 1, y, height, stretch));
            y += height + ItemGap;
            start = index + 1;
            ratioSum = 0;
        }

        return rows;
    }

    private static double ResolveWidth(double width) =>
        double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ? FallbackWidth : width;

    private readonly record struct GalleryRow(
        int StartIndex,
        int EndIndex,
        double Top,
        double Height,
        bool StretchToWidth)
    {
        public double Bottom => Top + Height;
    }
}
