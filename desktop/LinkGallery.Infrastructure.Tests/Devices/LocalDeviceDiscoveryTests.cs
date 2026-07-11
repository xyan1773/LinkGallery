using System.Net;
using LinkGallery.Infrastructure.Devices;

namespace LinkGallery.Infrastructure.Tests.Devices;

[TestClass]
public sealed class LocalDeviceDiscoveryTests
{
    [TestMethod]
    public void Ipv4AddressCodeRoundTripsTheCompleteAddress()
    {
        var code = Ipv4AddressCode.Encode(IPAddress.Parse("172.23.45.108"));

        Assert.AreEqual("AC172D6C", code);
        Assert.AreEqual("AC17-2D6C", Ipv4AddressCode.Format(code));
        Assert.IsTrue(Ipv4AddressCode.TryDecode("ac17-2d6c", out var decoded));
        Assert.AreEqual("172.23.45.108", decoded.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("123456")]
    [DataRow("AC17-ZD6C")]
    [DataRow("AC172D6C00")]
    public void Ipv4AddressCodeRejectsInvalidValues(string value)
    {
        Assert.IsFalse(Ipv4AddressCode.TryDecode(value, out _));
    }

}
