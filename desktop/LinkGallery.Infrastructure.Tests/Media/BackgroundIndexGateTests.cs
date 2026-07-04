using LinkGallery.Application.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class BackgroundIndexGateTests
{
    [TestMethod]
    public async Task EveryPauseReasonMustResumeBeforeWaitersContinue()
    {
        var gate = new BackgroundIndexGate();
        gate.Pause("preview");
        gate.Pause("transfer");

        var waiter = gate.WaitAsync();
        gate.Resume("preview");
        Assert.IsFalse(waiter.IsCompleted);

        gate.Resume("transfer");
        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(gate.IsPaused);
    }

    [TestMethod]
    public async Task PausedWaitCanBeCancelledWithoutResumingGate()
    {
        var gate = new BackgroundIndexGate();
        gate.Pause("offline");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => gate.WaitAsync(cancellation.Token));
        Assert.IsTrue(gate.IsPaused);
    }
}
