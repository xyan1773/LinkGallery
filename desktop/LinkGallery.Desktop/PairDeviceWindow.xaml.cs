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
    }

    public void Complete(DiscoveredDevice device, string pairingCode)
    {
        if (IsClosed) return;
        ResolvedDevice = device;
        ActiveCode = pairingCode;
        DialogResult = true;
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
            ? "用手机扫描二维码，或输入下方六位码"
            : "Scan with the phone, or enter the six-digit code below";
        PairingCodeLabel.Text = _useChinese ? "配对码" : "Pairing code";
        PairingCodeText.Text = ActiveCode.Length == 6
            ? $"{ActiveCode[..3]} {ActiveCode[3..]}"
            : ActiveCode;
        QrStatusText.Text = _useChinese ? "正在等待手机确认…" : "Waiting for the phone…";
        CodeHintText.Text = _useChinese
            ? "无法扫码时，在手机上输入六位码"
            : "If scanning fails, enter this code on the phone";
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
