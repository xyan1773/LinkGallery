using System.Net;
using LinkGallery.Domain.Devices;
using LinkGallery.Infrastructure.Devices;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class UdpDiscoveryClientTests
{
    [TestMethod]
    public void ParseAnnouncementUsesPacketSourceWhenHostIsMissing()
    {
        var device = UdpDiscoveryClient.ParseAnnouncement(
            """
            {
              "magic": "LINKGALLERY_DISCOVERY_V1",
              "type": "announce",
              "nonce": "n1",
              "deviceId": "phone-1",
              "name": "Pixel",
              "model": "Pixel 9",
              "host": "",
              "port": 39570,
              "apiVersion": 1,
              "instanceId": "instance-1",
              "pairingAvailable": true,
              "certificateFingerprint": "AA:BB",
              "timestamp": 2,
              "signature": ""
            }
            """,
            IPAddress.Parse("192.168.1.42"),
            "n1");

        Assert.IsNotNull(device);
        Assert.AreEqual("phone-1", device.DeviceId);
        Assert.AreEqual("192.168.1.42", device.Addresses.Single().Host);
        Assert.AreEqual(DeviceAddressSource.Udp, device.Addresses.Single().Source);
    }

    [TestMethod]
    public void ParseAnnouncementRejectsWrongNonce()
    {
        var device = UdpDiscoveryClient.ParseAnnouncement(
            """{"magic":"LINKGALLERY_DISCOVERY_V1","type":"announce","nonce":"other","deviceId":"phone-1"}""",
            IPAddress.Loopback,
            "n1");

        Assert.IsNull(device);
    }
}
