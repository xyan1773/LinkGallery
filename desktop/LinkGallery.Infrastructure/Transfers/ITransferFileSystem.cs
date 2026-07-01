namespace LinkGallery.Infrastructure.Transfers;

public interface ITransferFileSystem
{
    bool FileExists(string path);

    long GetFileLength(string path);

    void CreateDirectory(string path);

    Stream OpenPartialWrite(string path, long length);

    Stream OpenRead(string path);

    void Move(string sourcePath, string destinationPath);

    void Delete(string path);
}

internal sealed class PhysicalTransferFileSystem : ITransferFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream OpenPartialWrite(string path, long length)
    {
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.SetLength(length);
        stream.Position = length;
        return stream;
    }

    public Stream OpenRead(string path) =>
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Move(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath);

    public void Delete(string path) => File.Delete(path);
}
