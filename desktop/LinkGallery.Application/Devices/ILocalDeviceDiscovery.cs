using LinkGallery.Domain.Devices;

namespace LinkGallery.Application.Devices;

public interface ILocalDeviceDiscovery
{
    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        string desktopId,
        CancellationToken cancellationToken);

    Task<DiscoveredDevice?> ResolvePairingCodeAsync(
        string desktopId,
        string pairingCode,
        CancellationToken cancellationToken);
}
