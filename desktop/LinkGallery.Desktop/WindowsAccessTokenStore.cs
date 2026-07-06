using System.Security.Cryptography;
using System.Text;
using System.IO;
using LinkGallery.Application.Devices;

namespace LinkGallery.Desktop;

internal sealed class WindowsAccessTokenStore(string rootDirectory) : IAccessTokenStore
{
    private readonly string _rootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<string> SaveAsync(string accessToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        Directory.CreateDirectory(_rootDirectory);
        var key = Guid.NewGuid().ToString("N");
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(accessToken),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(PathFor(key), encrypted, cancellationToken).ConfigureAwait(false);
        return key;
    }

    public async Task<string?> ReadAsync(string credentialKey, CancellationToken cancellationToken)
    {
        var path = PathFor(credentialKey);
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var clear = ProtectedData.Unprotect(
            encrypted,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clear);
    }

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(credentialKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string credentialKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialKey);
        if (credentialKey.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Credential key is invalid.", nameof(credentialKey));
        }
        return Path.Combine(_rootDirectory, $"{credentialKey}.bin");
    }
}
