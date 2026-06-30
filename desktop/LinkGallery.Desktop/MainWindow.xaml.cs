using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Windows;
using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;
using LinkGallery.Infrastructure.Media;

namespace LinkGallery.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private CancellationTokenSource? _connectionCancellation;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        Disconnect();
        SetLoading(true, "正在连接手机并读取媒体…");
        _connectionCancellation = new CancellationTokenSource();

        try
        {
            var apiAddress = HttpReadOnlyMediaSource.NormalizeApiAddress(AddressTextBox.Text);
            var source = new HttpReadOnlyMediaSource(_httpClient, apiAddress);
            var device = await source.GetDeviceInfoAsync(_connectionCancellation.Token);
            var page = await source.GetMediaPageAsync(
                new MediaQuery(Limit: 100),
                _connectionCancellation.Token);

            DeviceNameText.Text = device.Name;
            DeviceModelText.Text = string.IsNullOrWhiteSpace(device.Model)
                ? device.Platform
                : $"{device.Model} · {device.Platform}";
            BatteryText.Text = device.BatteryPercent.HasValue ? $"电量 {device.BatteryPercent}%" : "电量未知";
            MediaCountText.Text = $"共 {device.MediaCount:N0} 项媒体";
            MediaGrid.ItemsSource = page.Items.Select(MediaRow.From).ToArray();
            DevicePanel.Visibility = Visibility.Visible;
            MediaGrid.Visibility = Visibility.Visible;
            EmptyText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "手机中没有可显示的照片或视频";
            StatusText.Text = page.NextCursor is null
                ? $"已连接 · 已加载 {page.Items.Count:N0} 项"
                : $"已连接 · 已加载首屏 {page.Items.Count:N0} 项";
            DisconnectButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "连接已取消";
        }
        catch (Exception exception)
        {
            ShowConnectionError(exception);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => Disconnect();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect();
        _httpClient.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Disconnect()
    {
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        DevicePanel.Visibility = Visibility.Collapsed;
        MediaGrid.Visibility = Visibility.Collapsed;
        MediaGrid.ItemsSource = null;
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

    private void ShowConnectionError(Exception exception)
    {
        DisconnectButton.IsEnabled = false;
        StatusText.Text = exception switch
        {
            FormatException => exception.Message,
            MediaSourceTimeoutException => "连接超时。请确认手机在线、地址正确，且两台设备在同一 Wi-Fi。",
            MediaSourceProtocolException => $"协议错误：{exception.Message}",
            MediaSourceHttpException { StatusCode: HttpStatusCode.Forbidden } =>
                "手机未授予照片和视频读取权限，请在手机端授权后重试。",
            MediaSourceHttpException { StatusCode: HttpStatusCode.BadRequest } =>
                $"手机拒绝了请求：{exception.Message}",
            MediaSourceHttpException => $"手机返回错误：{exception.Message}",
            HttpRequestException => "无法连接手机。请检查 IP、端口、Wi-Fi 和手机服务状态。",
            _ => $"连接失败：{exception.Message}",
        };
        EmptyText.Text = "未能加载媒体";
        EmptyText.Visibility = Visibility.Visible;
    }

    private sealed record MediaRow(
        string Type,
        string FileName,
        string DisplaySize,
        string Details,
        string TakenAt,
        string AlbumName)
    {
        public static MediaRow From(MediaItem item) => new(
            item.Type == MediaType.Image ? "图片" : "视频",
            item.FileName,
            FormatSize(item.FileSize),
            FormatDetails(item),
            item.TakenAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            item.AlbumName ?? "—");

        private static string FormatDetails(MediaItem item)
        {
            if (item.Type == MediaType.Video && item.DurationMilliseconds.HasValue)
            {
                return TimeSpan.FromMilliseconds(item.DurationMilliseconds.Value)
                    .ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            }

            return item.Width.HasValue && item.Height.HasValue ? $"{item.Width} × {item.Height}" : "—";
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
