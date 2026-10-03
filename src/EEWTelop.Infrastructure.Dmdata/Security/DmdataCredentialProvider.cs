using EEWTelop.Application.Configuration;

namespace EEWTelop.Infrastructure.Dmdata.Security;

internal sealed class DmdataCredential
{
    public DmdataCredential(
        DmdataAuthenticationMode authenticationMode,
        string secret)
    {
        AuthenticationMode = authenticationMode;
        Secret = secret;
    }

    public DmdataAuthenticationMode AuthenticationMode { get; }

    public string Secret { get; }
}

internal interface IDmdataCredentialProvider
{
    DmdataCredential GetCredential();

    ValueTask<DmdataCredential> GetCredentialAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetCredential());

    Task<bool> RefreshAfterUnauthorizedAsync(string rejectedToken, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

internal sealed class FixedDmdataCredentialProvider : IDmdataCredentialProvider
{
    private readonly DmdataCredential _credential;

    public FixedDmdataCredentialProvider(
        string secret,
        DmdataAuthenticationMode authenticationMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _credential = new DmdataCredential(authenticationMode, secret.Trim());
    }

    public DmdataCredential GetCredential() => _credential;
}

internal sealed class OAuthDmdataCredentialProvider(
    EEWTelop.Application.Abstractions.IDmdataOAuthService service,
    string clientId,
    IReadOnlyList<string> scopes) : IDmdataCredentialProvider
{
    public DmdataCredential GetCredential() =>
        throw new InvalidOperationException("OAuth credentials must be obtained asynchronously.");

    public async ValueTask<DmdataCredential> GetCredentialAsync(CancellationToken cancellationToken) =>
        new(DmdataAuthenticationMode.OAuthAccessToken,
            await service.GetAccessTokenAsync(clientId, scopes, cancellationToken).ConfigureAwait(false));

    public async Task<bool> RefreshAfterUnauthorizedAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        await service.GetAccessTokenAsync(clientId, scopes, cancellationToken, rejectedToken).ConfigureAwait(false);
        return true;
    }
}
