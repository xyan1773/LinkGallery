using LinkGallery.Domain.Transfers;

namespace LinkGallery.Domain.Tests.Transfers;

[TestClass]
public sealed class TransferJobTests
{
    [TestMethod]
    public void CompleteMarksTransferCompletedWhenAllBytesArrived()
    {
        var job = CreateJob(totalBytes: 100);

        job.Start();
        job.ReportProgress(100);
        job.Complete();

        Assert.AreEqual(TransferStatus.Completed, job.Status);
    }

    [TestMethod]
    public void CompleteThrowsWhenBytesAreMissing()
    {
        var job = CreateJob(totalBytes: 100);

        job.Start();
        job.ReportProgress(99);

        Assert.Throws<InvalidOperationException>(() => job.Complete());
    }

    [TestMethod]
    public void ReportProgressThrowsWhenProgressMovesBackwards()
    {
        var job = CreateJob(totalBytes: 100);
        job.Start();
        job.ReportProgress(50);

        Assert.Throws<ArgumentOutOfRangeException>(() => job.ReportProgress(49));
    }

    [TestMethod]
    public void PausedTransferCanResumeFromExistingOffset()
    {
        var job = CreateJob(totalBytes: 100);
        job.Start();
        job.ReportProgress(40);
        job.Pause();

        job.Resume();
        job.Start();
        job.ReportProgress(100);
        job.Complete();

        Assert.AreEqual(100, job.BytesTransferred);
        Assert.AreEqual(TransferStatus.Completed, job.Status);
    }

    [TestMethod]
    public void RunningSnapshotRecoversToPendingWithoutLosingProgress()
    {
        var job = CreateJob(totalBytes: 100);
        job.Start();
        job.ReportProgress(40);
        var restored = TransferJob.Restore(job.ToSnapshot());

        restored.RecoverAfterRestart();

        Assert.AreEqual(TransferStatus.Pending, restored.Status);
        Assert.AreEqual(40, restored.BytesTransferred);
    }

    [TestMethod]
    public void CancelledTransferRejectsFurtherTransitions()
    {
        var job = CreateJob(totalBytes: 100);
        job.Cancel();

        Assert.AreEqual(TransferStatus.Cancelled, job.Status);
        Assert.Throws<InvalidOperationException>(job.Start);
    }

    private static TransferJob CreateJob(long totalBytes) =>
        new(
            Guid.NewGuid(),
            "android-device",
            "media-item",
            @"D:\Photos\image.jpg",
            totalBytes);
}
