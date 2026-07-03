using LinkGallery.Domain.Devices;

namespace LinkGallery.Application.Devices;

public sealed class DiscoveryManager
{
    private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.Ordinal);

    public IReadOnlyList<DiscoveredDevice> Devices =>
        _devices.Values.OrderBy(device => device.DisplayName, StringComparer.CurrentCulture).ToArray();

    public DiscoveredDevice Merge(DiscoveredDevice discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentException.ThrowIfNullOrWhiteSpace(discovered.DeviceId);

        if (!_devices.TryGetValue(discovered.DeviceId, out var existing))
        {
            _devices[discovered.DeviceId] = discovered;
            return discovered;
        }

        existing.DisplayName = PreferRequired(discovered.DisplayName, existing.DisplayName);
        existing.Model = Prefer(discovered.Model, existing.Model);
        existing.Manufacturer = Prefer(discovered.Manufacturer, existing.Manufacturer);
        existing.CertificateFingerprint = Prefer(discovered.CertificateFingerprint, existing.CertificateFingerprint);
        existing.InstanceId = Prefer(discovered.InstanceId, existing.InstanceId);
        existing.PairingAvailable = existing.PairingAvailable || discovered.PairingAvailable;
        foreach (var address in discovered.Addresses)
        {
            if (!existing.Addresses.Any(candidate =>
                    string.Equals(candidate.Host, address.Host, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Port == address.Port))
            {
                existing.Addresses.Add(address);
            }
        }

        return existing;
    }

    public static DeviceTrustMatch MatchTrust(DiscoveredDevice discovered, PairedDevice? pairedDevice)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (pairedDevice is null)
        {
            return DeviceTrustMatch.Unpaired;
        }

        if (!string.Equals(discovered.DeviceId, pairedDevice.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(
                discovered.CertificateFingerprint,
                pairedDevice.CertificateFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return DeviceTrustMatch.IdentityChanged;
        }

        return DeviceTrustMatch.Trusted;
    }

    private static string? Prefer(string? candidate, string? fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;

    private static string PreferRequired(string candidate, string fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
}

public enum DeviceTrustMatch
{
    Unpaired,
    Trusted,
    IdentityChanged,
}

public static class MdnsDiscoveredDeviceMapper
{
    public static DiscoveredDevice FromTxt(
        string host,
        int port,
        IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(attributes);
        var deviceId = Require(attributes, "id");
        var name = attributes.TryGetValue("name", out var rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : deviceId;
        var device = new DiscoveredDevice
        {
            DeviceId = deviceId,
            DisplayName = name,
            Model = attributes.GetValueOrDefault("model"),
            CertificateFingerprint = attributes.GetValueOrDefault("fp"),
            InstanceId = attributes.GetValueOrDefault("instance"),
            PairingAvailable = string.Equals(
                attributes.GetValueOrDefault("pairing"),
                "available",
                StringComparison.OrdinalIgnoreCase),
        };
        device.Addresses.Add(
            new DeviceAddress
            {
                DeviceId = deviceId,
                Host = host,
                Port = port,
                Source = DeviceAddressSource.Mdns,
            });
        return device;
    }

    private static string Require(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"mDNS result is missing TXT attribute '{name}'.");
}
