using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkGallery.Application.Devices;
using LinkGallery.Domain.Devices;

namespace LinkGallery.Infrastructure.Devices;

public sealed class HttpPublicDeviceInfoClient(HttpClient httpClient) : IPublicDeviceInfoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<PublicDeviceInfo> GetAsync(Uri apiBaseAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        var address = EnsureTrailingSlash(apiBaseAddress);
        var dto = await httpClient.GetFromJsonAsync<PublicDeviceInfoDto>(
            new Uri(address, "public/info"),
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The device returned an empty public info response.");

        if (string.IsNullOrWhiteSpace(dto.DeviceId) ||
            string.IsNullOrWhiteSpace(dto.DeviceName) ||
            string.IsNullOrWhiteSpace(dto.CertificateFingerprint))
        {
            throw new InvalidOperationException("The device returned incomplete public identity information.");
        }

        return new PublicDeviceInfo
        {
            DeviceId = dto.DeviceId,
            DeviceName = dto.DeviceName,
            Manufacturer = dto.Manufacturer,
            Model = dto.Model,
            ApiVersion = dto.ApiVersion,
            ServerVersion = dto.ServerVersion,
            InstanceId = dto.InstanceId,
            PairingAvailable = dto.PairingAvailable,
            CertificateFingerprint = dto.CertificateFingerprint,
        };
    }

    private static Uri EnsureTrailingSlash(Uri address) =>
        address.AbsoluteUri.EndsWith('/')
            ? address
            : new Uri($"{address.AbsoluteUri}/");

    private sealed record PublicDeviceInfoDto(
        string DeviceId,
        string DeviceName,
        string? Manufacturer,
        string? Model,
        int ApiVersion,
        string? ServerVersion,
        string? InstanceId,
        bool PairingAvailable,
        string CertificateFingerprint);
}
