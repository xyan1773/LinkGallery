using System.Net;
using LinkGallery.Application.Devices;
using LinkGallery.Infrastructure.Devices;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class LocalDeviceDiscoveryTests
{
    [TestMethod]
    public void Same24ProbeIsBoundedAndExcludesCurrentHost()
    {
        var hosts = LocalDeviceDiscovery.HostsInSame24(IPAddress.Parse("172.23.45.108")).ToArray();

        Assert.HasCount(253, hosts);
        Assert.DoesNotContain(IPAddress.Parse("172.23.45.108"), hosts);
        Assert.Contains(IPAddress.Parse("172.23.45.1"), hosts);
        Assert.Contains(IPAddress.Parse("172.23.45.254"), hosts);
    }

    [TestMethod]
    public void PairingQrContainsVersionedEscapedIdentityAndRandomCode()
    {
        var payload = PairingQrPayloadCodec.Create("desktop id", "Living Room PC", "281604");

        StringAssert.StartsWith(payload, "linkgallery://pair?v=1");
        StringAssert.Contains(payload, "code=281604");
        StringAssert.Contains(payload, "desktopId=desktop%20id");
        StringAssert.Contains(payload, "desktopName=Living%20Room%20PC");
    }
}
