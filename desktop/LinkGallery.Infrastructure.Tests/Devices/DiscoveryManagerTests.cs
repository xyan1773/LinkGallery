using LinkGallery.Application.Devices;
using LinkGallery.Domain.Devices;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class DiscoveryManagerTests
{
    [TestMethod]
    public void MergeUsesDeviceIdInsteadOfNameOrAddress()
    {
        var manager = new DiscoveryManager();
        var first = Device("phone-1", "Pixel", "192.168.1.10", DeviceAddressSource.Mdns);
        var second = Device("phone-1", "Renamed Pixel", "192.168.1.11", DeviceAddressSource.Saved);
        var sameNameDifferentIdentity = Device("phone-2", "Pixel", "192.168.1.10", DeviceAddressSource.Mdns);

        manager.Merge(first);
        manager.Merge(second);
        manager.Merge(sameNameDifferentIdentity);

        Assert.AreEqual(2, manager.Devices.Count);
        var merged = manager.Devices.Single(device => device.DeviceId == "phone-1");
        Assert.AreEqual("Renamed Pixel", merged.DisplayName);
        Assert.AreEqual(2, merged.Addresses.Count);
    }

    [TestMethod]
    public void TrustMatchDetectsUnpairedTrustedAndIdentityChanged()
    {
        var manager = new DiscoveryManager();
        var discovered = Device("phone-1", "Pixel", "192.168.1.10", DeviceAddressSource.Mdns);
        discovered.CertificateFingerprint = "AA:BB";

        Assert.AreEqual(DeviceTrustMatch.Unpaired, DiscoveryManager.MatchTrust(discovered, null));
        Assert.AreEqual(DeviceTrustMatch.Trusted, DiscoveryManager.MatchTrust(discovered, Paired("AA:BB")));
        Assert.AreEqual(DeviceTrustMatch.IdentityChanged, DiscoveryManager.MatchTrust(discovered, Paired("CC:DD")));
    }

    [TestMethod]
    public void MdnsTxtMapsToDiscoveredDevice()
    {
        var discovered = MdnsDiscoveredDeviceMapper.FromTxt(
            "192.168.1.42",
            39570,
            new Dictionary<string, string>
            {
                ["id"] = "phone-1",
                ["name"] = "Pixel",
                ["model"] = "Pixel 9",
                ["instance"] = "instance-1",
                ["pairing"] = "available",
                ["fp"] = "AA:BB",
            });

        Assert.AreEqual("phone-1", discovered.DeviceId);
        Assert.AreEqual("Pixel", discovered.DisplayName);
        Assert.AreEqual("Pixel 9", discovered.Model);
        Assert.IsTrue(discovered.PairingAvailable);
        Assert.AreEqual(DeviceAddressSource.Mdns, discovered.Addresses.Single().Source);
    }

    private static DiscoveredDevice Device(
        string id,
        string name,
        string host,
        DeviceAddressSource source)
    {
        var device = new DiscoveredDevice
        {
            DeviceId = id,
            DisplayName = name,
            CertificateFingerprint = "AA:BB",
        };
        device.Addresses.Add(
            new DeviceAddress
            {
                DeviceId = id,
                Host = host,
                Port = 39570,
                Source = source,
            });
        return device;
    }

    private static PairedDevice Paired(string fingerprint) => new()
    {
        DeviceId = "phone-1",
        DisplayName = "Pixel",
        IdentityPublicKey = "public-key",
        CertificateFingerprint = fingerprint,
        CredentialKey = "credential-phone-1",
    };
}
