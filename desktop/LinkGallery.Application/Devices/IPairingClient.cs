namespace LinkGallery.Application.Devices;

public interface IPairingClient
{
    Task<PairingSession> StartAsync(
        Uri apiBaseAddress,
        PairingIdentity identity,
        CancellationToken cancellationToken);

    Task<PairingCredential> ConfirmAsync(
        Uri apiBaseAddress,
        string pairingSessionId,
        string verificationCode,
        CancellationToken cancellationToken);

    Task RevokeAsync(
        Uri apiBaseAddress,
        string accessToken,
        CancellationToken cancellationToken);
}

public sealed record PairingIdentity(
    string DesktopId,
    string DesktopName,
    string? DesktopModel,
    string IdentityPublicKey,
    string EphemeralPublicKey,
    string Nonce);

public sealed record PairingSession(
    string PairingSessionId,
    DateTimeOffset ExpiresAt,
    int AttemptsRemaining,
    int CodeLength);

public sealed record PairingCredential(string AccessToken, string TokenType);

public interface IAccessTokenStore
{
    Task<string> SaveAsync(string accessToken, CancellationToken cancellationToken);

    Task<string?> ReadAsync(string credentialKey, CancellationToken cancellationToken);

    Task DeleteAsync(string credentialKey, CancellationToken cancellationToken);
}
