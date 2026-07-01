namespace LinkGallery.Application.Media;

public sealed class VideoPlaybackState
{
    private VideoPlaybackStatus _statusBeforeSeek;

    public VideoPlaybackStatus Status { get; private set; } = VideoPlaybackStatus.Idle;

    public bool ResumeAfterSeek { get; private set; }

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

    public void BeginSeek()
    {
        Ensure(
            VideoPlaybackStatus.Ready,
            VideoPlaybackStatus.Playing,
            VideoPlaybackStatus.Paused,
            VideoPlaybackStatus.Ended);
        _statusBeforeSeek = Status;
        ResumeAfterSeek = Status == VideoPlaybackStatus.Playing;
        Status = VideoPlaybackStatus.Seeking;
    }

    public void CompleteSeek()
    {
        Ensure(VideoPlaybackStatus.Seeking);
        Status = _statusBeforeSeek switch
        {
            VideoPlaybackStatus.Playing => VideoPlaybackStatus.Playing,
            VideoPlaybackStatus.Ready => VideoPlaybackStatus.Ready,
            _ => VideoPlaybackStatus.Paused,
        };
        ResumeAfterSeek = false;
    }

    public void MarkFailed() => Status = VideoPlaybackStatus.Failed;

    public void Reset()
    {
        Status = VideoPlaybackStatus.Idle;
        ResumeAfterSeek = false;
    }

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
    Seeking,
    Ended,
    Failed,
}
