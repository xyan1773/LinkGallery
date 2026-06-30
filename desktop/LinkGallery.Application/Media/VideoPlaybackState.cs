namespace LinkGallery.Application.Media;

public sealed class VideoPlaybackState
{
    public VideoPlaybackStatus Status { get; private set; } = VideoPlaybackStatus.Idle;

    public bool CanControl =>
        Status is VideoPlaybackStatus.Ready or
            VideoPlaybackStatus.Playing or
            VideoPlaybackStatus.Paused or
            VideoPlaybackStatus.Ended;

    public void BeginLoading() => Status = VideoPlaybackStatus.Loading;

    public void MarkReady()
    {
        Ensure(VideoPlaybackStatus.Loading);
        Status = VideoPlaybackStatus.Ready;
    }

    public void Play()
    {
        Ensure(
            VideoPlaybackStatus.Ready,
            VideoPlaybackStatus.Paused,
            VideoPlaybackStatus.Ended);
        Status = VideoPlaybackStatus.Playing;
    }

    public void Pause()
    {
        Ensure(VideoPlaybackStatus.Playing);
        Status = VideoPlaybackStatus.Paused;
    }

    public void MarkEnded()
    {
        Ensure(VideoPlaybackStatus.Playing);
        Status = VideoPlaybackStatus.Ended;
    }

    public void MarkFailed() => Status = VideoPlaybackStatus.Failed;

    public void Reset() => Status = VideoPlaybackStatus.Idle;

    private void Ensure(params VideoPlaybackStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException(
                $"Video playback cannot transition from {Status} in this operation.");
        }
    }
}

public enum VideoPlaybackStatus
{
    Idle,
    Loading,
    Ready,
    Playing,
    Paused,
    Ended,
    Failed,
}
