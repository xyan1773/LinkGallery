using LinkGallery.Application.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class MediaDevicePresentationTests
{
    [TestMethod]
    public void OfflineMediaUsesItsExactPairedPhoneAmongMultipleDevices()
    {
        var paired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["phone-1"] = "Pixel personal",
            ["phone-2"] = "Pixel studio",
        };

        var result = MediaDevicePresentation.Resolve(
            "phone-2",
            connectedDeviceId: null,
            connectedDeviceName: null,
            paired,
            "Saved device");

        Assert.AreEqual("Pixel studio", result);
    }

    [TestMethod]
    public void ConnectedNameWinsOnlyForMatchingDevice()
    {
        var paired = new Dictionary<string, string> { ["phone-2"] = "Saved studio" };

        Assert.AreEqual(
            "Live studio",
            MediaDevicePresentation.Resolve("phone-2", "phone-2", "Live studio", paired, "Unknown"));
        Assert.AreEqual(
            "Unknown",
            MediaDevicePresentation.Resolve("phone-1", "phone-2", "Live studio", paired, "Unknown"));
    }
}
