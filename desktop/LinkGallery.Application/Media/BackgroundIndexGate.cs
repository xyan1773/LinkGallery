namespace LinkGallery.Application.Media;

public sealed class BackgroundIndexGate
{
    private readonly object _sync = new();
    private readonly HashSet<string> _pauseReasons = new(StringComparer.Ordinal);
    private TaskCompletionSource _resumed = CompletedSource();

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _pauseReasons.Count > 0;
            }
        }
    }

    public void Pause(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            if (_pauseReasons.Count == 0)
            {
                _resumed = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _pauseReasons.Add(reason);
        }
    }

    public void Resume(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            if (!_pauseReasons.Remove(reason) || _pauseReasons.Count != 0)
            {
                return;
            }
            _resumed.TrySetResult();
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return _pauseReasons.Count == 0
                ? Task.CompletedTask
                : _resumed.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
