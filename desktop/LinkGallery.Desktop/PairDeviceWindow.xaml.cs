using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LinkGallery.Domain.Devices;
using QRCoder;

namespace LinkGallery.Desktop;

public partial class PairDeviceWindow : Window
{
    private readonly bool _useChinese;

    public PairDeviceWindow(string qrPayload, string initialCode, bool useChinese)
    {
        InitializeComponent();
        _useChinese = useChinese;
        ActiveCode = initialCode;
        QrImage.Source = CreateQrImage(qrPayload);
        ApplyLanguage();
        Closed += (_, _) => IsClosed = true;
    }

    public string ActiveCode { get; private set; }
    public DiscoveredDevice? ResolvedDevice { get; private set; }
    public bool ManualIpRequested { get; private set; }
    public bool IsClosed { get; private set; }

    public void SetWaitingStatus(string message)
    {
        if (IsClosed) return;
        QrStatusText.Text = message;
        ManualErrorText.Text = ManualCodePanel.Visibility == Visibility.Visible ? message : string.Empty;
    }

    public void Complete(DiscoveredDevice device, string pairingCode)
    {
        if (IsClosed) return;
        ResolvedDevice = device;
        ActiveCode = pairingCode;
        DialogResult = true;
    }

    private void OnCannotScanClick(object sender, RoutedEventArgs e)
    {
        QrPanel.Visibility = Visibility.Collapsed;
        ManualCodePanel.Visibility = Visibility.Visible;
        CannotScanButton.Visibility = Visibility.Collapsed;
        CodeTextBox.Focus();
    }

    private void OnConfirmManualCode(object sender, RoutedEventArgs e)
    {
        var code = CodeTextBox.Text.Trim();
        if (code.Length != 6 || code.Any(character => !char.IsAsciiDigit(character)))
        {
            ManualErrorText.Text = _useChinese
                ? "请输入手机上显示的六位数字。"
                : "Enter the six digits shown on the phone.";
            return;
        }
        ActiveCode = code;
        ManualErrorText.Text = _useChinese
            ? "正在局域网中查找手机…"
            : "Searching for the phone on the local network…";
    }

    private void OnManualIpClick(object sender, RoutedEventArgs e)
    {
        ManualIpRequested = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void ApplyLanguage()
    {
        Title = _useChinese ? "配对手机" : "Pair phone";
        TitleText.Text = _useChinese ? "配对手机" : "Pair phone";
        SubtitleText.Text = _useChinese
            ? "在手机 LinkGallery 的设备页扫描二维码"
            : "Scan this QR code from the Devices page on the phone";
        QrStatusText.Text = _useChinese ? "正在等待手机扫描…" : "Waiting for the phone to scan…";
        ManualTitleText.Text = _useChinese ? "输入手机上的六位码" : "Enter the six-digit phone code";
        ManualBodyText.Text = _useChinese
            ? "手机打开配对窗口后会显示一次性六位码，电脑会在当前局域网中自动找到它。"
            : "Open pairing on the phone, then enter its one-time code. LinkGallery will find it on this network.";
        FindByCodeButton.Content = _useChinese ? "查找并配对" : "Find and pair";
        CannotScanButton.Content = _useChinese ? "无法扫描二维码" : "Can't scan the QR code";
        ManualIpButton.Content = _useChinese ? "输入 IP" : "Enter IP";
    }

    private static BitmapImage CreateQrImage(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(12);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
