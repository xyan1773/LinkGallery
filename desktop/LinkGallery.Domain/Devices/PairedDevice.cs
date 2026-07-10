namespace LinkGallery.Domain.Devices;

public enum PairedDeviceStatus
{
    Checking,
    Online,
    Offline,
    Pairing,
    Unpaired,
    IdentityChanged,
    AuthExpired,
    Error,
}

public enum DeviceAddressSource
{
    Saved,
    Mdns,
    Udp,
    Subnet,
    Adb,
    Manual,
    Qr,
}

public sealed class PairedDevice
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public required string IdentityPublicKey { get; init; }
    public required string CertificateFingerprint { get; init; }
    public required string CredentialKey { get; init; }
    public string? LastHost { get; set; }
    public int? LastPort { get; set; }
    public string? LastInstanceId { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public bool AutoConnect { get; set; }
    public PairedDeviceStatus Status { get; set; } = PairedDeviceStatus.Offline;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class DeviceAddress
{
    public required string DeviceId { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required DeviceAddressSource Source { get; init; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
}

public sealed class PublicDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public int ApiVersion { get; init; }
    public string? ServerVersion { get; init; }
    public string? InstanceId { get; init; }
    public bool PairingAvailable { get; init; }
    public required string CertificateFingerprint { get; init; }
}
