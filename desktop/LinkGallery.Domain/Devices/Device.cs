namespace LinkGallery.Domain.Devices;

public sealed class Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public string? Model { get; init; }
    public int? BatteryPercent { get; init; }
    public int MediaCount { get; init; }
    public Uri? Address { get; set; }
    public bool IsOnline { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
