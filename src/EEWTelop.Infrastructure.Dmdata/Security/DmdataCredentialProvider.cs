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
