using LinkGallery.Domain.Devices;

namespace LinkGallery.Application.Devices;

public interface IPairedDeviceStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PairedDevice>> ListPairedDevicesAsync(CancellationToken cancellationToken);

    Task UpsertPairedDeviceAsync(PairedDevice device, CancellationToken cancellationToken);

    Task UpsertAddressAsync(DeviceAddress address, CancellationToken cancellationToken);

    Task UpdateProbeSuccessAsync(
        PairedDevice device,
        string host,
        int port,
        string? instanceId,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken);

    Task UpdateProbeFailureAsync(
        string deviceId,
        string host,
        int port,
        PairedDeviceStatus status,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);
}

public interface IPublicDeviceInfoClient
{
    Task<PublicDeviceInfo> GetAsync(Uri apiBaseAddress, CancellationToken cancellationToken);
}

public sealed class SavedAddressProbe(IPublicDeviceInfoClient publicInfoClient)
{
    public async Task<PairedDevice> ProbeAsync(
        PairedDevice device,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(device.LastHost) || !device.LastPort.HasValue)
        {
            return WithStatus(device, PairedDeviceStatus.Offline);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var address = new UriBuilder(Uri.UriSchemeHttp, device.LastHost, device.LastPort.Value, "api/v1/").Uri;
            var info = await publicInfoClient.GetAsync(address, timeoutSource.Token).ConfigureAwait(false);
            if (!string.Equals(info.DeviceId, device.DeviceId, StringComparison.Ordinal) ||
                !string.Equals(info.CertificateFingerprint, device.CertificateFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return WithStatus(device, PairedDeviceStatus.IdentityChanged);
            }

            var updated = Copy(device);
            updated.DisplayName = string.IsNullOrWhiteSpace(info.DeviceName) ? updated.DisplayName : info.DeviceName;
            updated.Manufacturer = info.Manufacturer;
            updated.Model = info.Model;
            updated.LastInstanceId = info.InstanceId;
            updated.LastSeenAt = DateTimeOffset.UtcNow;
            updated.Status = PairedDeviceStatus.Online;
            return updated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WithStatus(device, PairedDeviceStatus.Offline);
        }
        catch (HttpRequestException)
        {
            return WithStatus(device, PairedDeviceStatus.Offline);
        }
    }

    private static PairedDevice WithStatus(PairedDevice device, PairedDeviceStatus status)
    {
        var updated = Copy(device);
        updated.Status = status;
        return updated;
    }

    private static PairedDevice Copy(PairedDevice device) => new()
    {
        DeviceId = device.DeviceId,
        DisplayName = device.DisplayName,
        Manufacturer = device.Manufacturer,
        Model = device.Model,
        IdentityPublicKey = device.IdentityPublicKey,
        CertificateFingerprint = device.CertificateFingerprint,
        CredentialKey = device.CredentialKey,
        LastHost = device.LastHost,
        LastPort = device.LastPort,
        LastInstanceId = device.LastInstanceId,
        LastSeenAt = device.LastSeenAt,
        LastConnectedAt = device.LastConnectedAt,
        AutoConnect = device.AutoConnect,
        Status = device.Status,
        CreatedAt = device.CreatedAt,
    };
}
