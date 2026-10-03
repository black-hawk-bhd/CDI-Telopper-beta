namespace EEWTelop.Application.Abstractions;

public sealed record DmdataOAuthSessionInfo(
    string ClientId,
    string Scope,
    DateTimeOffset ExpiresAtUtc);

public interface IDmdataOAuthService
{
    DmdataOAuthSessionInfo? Session { get; }

    Task AuthorizeAsync(
        string clientId,
        IReadOnlyList<string> scopes,
        Action<Uri> openBrowser,
        CancellationToken cancellationToken);

    Task<string> GetAccessTokenAsync(
        string clientId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken,
        string? rejectedAccessToken = null);

    Task RevokeAsync(CancellationToken cancellationToken);
}
