using LinkGallery.Application.Media;

namespace LinkGallery.Infrastructure.Tests.Media;

[TestClass]
public sealed class VideoPlaybackStateTests
{
    [TestMethod]
    public void ReadyPlayPauseSeekResumeAndReplayUseRealPlayerStates()
    {
        var state = new VideoPlaybackState();

        state.BeginLoading();
        Assert.IsFalse(state.CanControl);
        state.MarkReady();
        Assert.IsTrue(state.CanControl);

        state.Play();
        Assert.AreEqual(VideoPlaybackStatus.Playing, state.Status);
        state.Pause();
        Assert.AreEqual(VideoPlaybackStatus.Paused, state.Status);
        state.Play();
        state.MarkEnded();
        Assert.AreEqual(VideoPlaybackStatus.Ended, state.Status);
        state.Play();
        Assert.AreEqual(VideoPlaybackStatus.Playing, state.Status);
    }

    [TestMethod]
    public void LoadingAndFailureCannotPretendToBePlaying()
    {
        var state = new VideoPlaybackState();

        state.BeginLoading();
        Assert.Throws<InvalidOperationException>(state.Play);
        state.MarkFailed();

        Assert.AreEqual(VideoPlaybackStatus.Failed, state.Status);
        Assert.IsFalse(state.CanControl);
    }
}
