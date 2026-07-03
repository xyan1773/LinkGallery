namespace LinkGallery.Domain.Devices;

public sealed class DiscoveredDevice
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; set; }
    public string? Model { get; set; }
    public string? Manufacturer { get; set; }
    public string? CertificateFingerprint { get; set; }
    public string? InstanceId { get; set; }
    public bool PairingAvailable { get; set; }
    public List<DeviceAddress> Addresses { get; } = [];
}
