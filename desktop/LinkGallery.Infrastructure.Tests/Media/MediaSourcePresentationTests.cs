using LinkGallery.Application.Media;
using LinkGallery.Domain.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class MediaSourcePresentationTests
{
    [TestMethod]
    public void SourceDescriptionKeepsCameraApplicationAndEditEvidenceSeparateFromPhone()
    {
        var item = Item("DJI Pocket 3", "DJI Mimo", isEditedExport: true);

        Assert.AreEqual(
            "DJI Pocket 3 · DJI Mimo · Edited export",
            MediaSourcePresentation.Describe(item, "Unknown", "Edited export"));
        Assert.AreEqual("phone-1", item.DeviceId);
    }

    [TestMethod]
    public void MissingEvidenceIsPresentedAsUnknown()
    {
        Assert.AreEqual(
            "Unknown",
            MediaSourcePresentation.Describe(Item(), "Unknown", "Edited export"));
    }

    private static MediaItem Item(
        string? sourceDevice = null,
        string? sourceApplication = null,
        bool isEditedExport = false) => new()
    {
        DeviceId = "phone-1",
        RemoteId = "media-1",
        FileName = "sample.jpg",
        Type = MediaType.Image,
        FileSize = 1,
        TakenAt = DateTimeOffset.UnixEpoch,
        ModifiedAt = DateTimeOffset.UnixEpoch,
        SourceDevice = sourceDevice,
        SourceApplication = sourceApplication,
        IsEditedExport = isEditedExport,
    };
}
