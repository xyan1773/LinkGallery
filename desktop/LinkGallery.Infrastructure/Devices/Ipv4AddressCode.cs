using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace LinkGallery.Infrastructure.Devices;

public static class Ipv4AddressCode
{
    public static string Encode(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses can be encoded.", nameof(address));
        }

        return Convert.ToHexString(address.GetAddressBytes());
    }

    public static bool TryEncode(string address, out string code)
    {
        code = string.Empty;
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        code = Encode(parsed);
        return true;
    }

    public static bool TryDecode(string value, out IPAddress address)
    {
        address = IPAddress.None;
        if (!TryNormalize(value, out var normalized) ||
            !uint.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var encoded))
        {
            return false;
        }

        address = new IPAddress(
        [
            (byte)(encoded >> 24),
            (byte)(encoded >> 16),
            (byte)(encoded >> 8),
            (byte)encoded,
        ]);
        return true;
    }

    public static string Format(string code)
    {
        if (!TryNormalize(code, out var normalized)) return code;
        return normalized.Length == 8 ? $"{normalized[..4]}-{normalized[4..]}" : normalized;
    }

    public static bool TryNormalize(string value, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                !Uri.IsHexDigit(character) && character != '-' && !char.IsWhiteSpace(character)))
        {
            return false;
        }

        code = new string(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return code.Length == 8;
    }
}
