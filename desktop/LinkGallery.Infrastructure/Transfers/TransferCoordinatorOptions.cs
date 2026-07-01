namespace LinkGallery.Infrastructure.Transfers;

public sealed class TransferCoordinatorOptions
{
    public int MaxConcurrentTransfers { get; init; } = 2;

    public int MaxRetries { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public bool ComputeSha256 { get; init; } = true;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentTransfers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrentTransfers, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries);
        if (InitialRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay));
        }
    }
}
