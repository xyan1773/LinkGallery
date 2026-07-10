namespace LinkGallery.Application.Devices;

public static class PairingQrPayloadCodec
{
    public static string Create(
        string desktopId,
        string desktopName,
        string verificationCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);
        if (verificationCode.Length != 6 ||
            verificationCode.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("Pairing code must contain exactly six digits.", nameof(verificationCode));
        }

        return "linkgallery://pair?v=1" +
            $"&code={Uri.EscapeDataString(verificationCode)}" +
            $"&desktopId={Uri.EscapeDataString(desktopId)}" +
            $"&desktopName={Uri.EscapeDataString(desktopName)}";
    }
}
