using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkGallery.Application.Devices;

namespace LinkGallery.Infrastructure.Devices;

public sealed class HttpPairingClient(HttpClient httpClient) : IPairingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<PairingSession> StartAsync(
        Uri apiBaseAddress,
        PairingIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(identity);
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(EnsureTrailingSlash(apiBaseAddress), "pair/start"),
            identity,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        var dto = await ReadAsync<PairStartResponse>(response, cancellationToken).ConfigureAwait(false);
        return new PairingSession(
            dto.PairingSessionId,
            DateTimeOffset.FromUnixTimeMilliseconds(dto.ExpiresAtEpochMillis),
            dto.AttemptsRemaining,
            dto.CodeLength);
    }

    public async Task<PairingCredential> ConfirmAsync(
        Uri apiBaseAddress,
        string pairingSessionId,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(EnsureTrailingSlash(apiBaseAddress), "pair/confirm"),
            new { pairingSessionId, verificationCode },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        var dto = await ReadAsync<PairConfirmResponse>(response, cancellationToken).ConfigureAwait(false);
        if (!dto.Paired || string.IsNullOrWhiteSpace(dto.AccessToken))
        {
            throw new InvalidOperationException("The phone did not return a valid pairing credential.");
        }
        return new PairingCredential(dto.AccessToken, dto.TokenType);
    }

    public async Task RevokeAsync(
        Uri apiBaseAddress,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(EnsureTrailingSlash(apiBaseAddress), "pair/revoke"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The phone returned an empty pairing response.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var problem = await response.Content.ReadFromJsonAsync<Problem>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            problem is null
                ? $"Pairing failed with HTTP {(int)response.StatusCode}."
                : $"{problem.Code}: {problem.Message}",
            null,
            response.StatusCode);
    }

    private static Uri EnsureTrailingSlash(Uri address) =>
        address.AbsoluteUri.EndsWith('/') ? address : new Uri($"{address.AbsoluteUri}/");

    private sealed record PairStartResponse(
        string PairingSessionId,
        string PhoneNonce,
        long ExpiresAtEpochMillis,
        int AttemptsRemaining,
        int CodeLength);

    private sealed record PairConfirmResponse(bool Paired, string AccessToken, string TokenType);

    private sealed record Problem(string Code, string Message);
}
