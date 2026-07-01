using System.Text.Json;
using LinkGallery.Application.Transfers;
using LinkGallery.Domain.Transfers;

namespace LinkGallery.Infrastructure.Transfers;

public sealed class JsonTransferJobStore : ITransferJobStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonTransferJobStore(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = Path.GetFullPath(storePath);
    }

    public async Task<IReadOnlyList<TransferJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadSnapshotsAsync(cancellationToken).ConfigureAwait(false))
                .Select(TransferJob.Restore)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TransferJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = await ReadSnapshotsAsync(cancellationToken).ConfigureAwait(false);
            var index = snapshots.FindIndex(candidate => candidate.Id == job.Id);
            if (index < 0)
            {
                snapshots.Add(job.ToSnapshot());
            }
            else
            {
                snapshots[index] = job.ToSnapshot();
            }

            await WriteSnapshotsAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<List<TransferJobSnapshot>> ReadSnapshotsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<TransferJobSnapshot>>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The persisted transfer queue is not valid JSON.", exception);
        }
    }

    private async Task WriteSnapshotsAsync(
        IReadOnlyList<TransferJobSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var temporaryPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshots,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _storePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
