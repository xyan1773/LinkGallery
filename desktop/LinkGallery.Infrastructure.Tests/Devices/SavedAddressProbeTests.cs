using LinkGallery.Application.Devices;
using LinkGallery.Domain.Devices;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class SavedAddressProbeTests
{
    [TestMethod]
    public async Task MatchingIdentityMarksDeviceOnline()
    {
        var probe = new SavedAddressProbe(
            new FakePublicDeviceInfoClient(
                new PublicDeviceInfo
                {
                    DeviceId = "phone-1",
                    DeviceName = "Pixel",
                    Manufacturer = "Google",
                    Model = "Pixel 9",
                    ApiVersion = 1,
                    ServerVersion = "0.1.0",
                    InstanceId = "instance-2",
                    PairingAvailable = false,
                    CertificateFingerprint = "AA:BB",
                }));

        var result = await probe.ProbeAsync(Device(), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.AreEqual(PairedDeviceStatus.Online, result.Status);
        Assert.AreEqual("instance-2", result.LastInstanceId);
        Assert.IsNotNull(result.LastSeenAt);
    }

    [TestMethod]
    public async Task FingerprintMismatchMarksIdentityChanged()
    {
        var probe = new SavedAddressProbe(
            new FakePublicDeviceInfoClient(
                new PublicDeviceInfo
                {
                    DeviceId = "phone-1",
                    DeviceName = "Pixel",
                    ApiVersion = 1,
                    PairingAvailable = false,
                    CertificateFingerprint = "CC:DD",
                }));

        var result = await probe.ProbeAsync(Device(), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.AreEqual(PairedDeviceStatus.IdentityChanged, result.Status);
    }

    [TestMethod]
    public async Task NetworkFailureKeepsDeviceOffline()
    {
        var probe = new SavedAddressProbe(new ThrowingPublicDeviceInfoClient());

        var result = await probe.ProbeAsync(Device(), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.AreEqual(PairedDeviceStatus.Offline, result.Status);
    }

    private static PairedDevice Device() => new()
    {
        DeviceId = "phone-1",
        DisplayName = "Pixel",
        IdentityPublicKey = "public-key",
        CertificateFingerprint = "AA:BB",
        CredentialKey = "credential-phone-1",
        LastHost = "192.168.1.20",
        LastPort = 39570,
    };

    private sealed class FakePublicDeviceInfoClient(PublicDeviceInfo info) : IPublicDeviceInfoClient
    {
        public Task<PublicDeviceInfo> GetAsync(Uri apiBaseAddress, CancellationToken cancellationToken) =>
            Task.FromResult(info);
    }

    private sealed class ThrowingPublicDeviceInfoClient : IPublicDeviceInfoClient
    {
        public Task<PublicDeviceInfo> GetAsync(Uri apiBaseAddress, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
