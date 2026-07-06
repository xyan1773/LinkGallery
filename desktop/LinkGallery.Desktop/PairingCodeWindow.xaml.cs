using System.Windows;

namespace LinkGallery.Desktop;

public partial class PairingCodeWindow : Window
{
    public PairingCodeWindow(int codeLength)
    {
        InitializeComponent();
        CodeTextBox.MaxLength = codeLength;
        Loaded += (_, _) => CodeTextBox.Focus();
    }

    public string VerificationCode => CodeTextBox.Text.Trim();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (CodeTextBox.Text.Length != CodeTextBox.MaxLength ||
            CodeTextBox.Text.Any(character => !char.IsAsciiDigit(character)))
        {
            ErrorText.Text = $"Enter the {CodeTextBox.MaxLength}-digit code shown on the phone.";
            return;
        }
        DialogResult = true;
    }
}
