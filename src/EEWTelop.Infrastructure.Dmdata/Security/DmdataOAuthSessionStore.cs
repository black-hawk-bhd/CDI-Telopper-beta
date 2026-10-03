using System.Security.Cryptography;
using System.Text.Json;

namespace EEWTelop.Infrastructure.Dmdata.Security;

internal sealed record DmdataOAuthTokens(
    string ClientId,
    string AccessToken,
    string RefreshToken,
    string Scope,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => "DMDATA OAuth session (tokens hidden)";
}

internal sealed class DmdataOAuthSessionStore(string path)
{
    public DmdataOAuthTokens? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            StoredSession? stored = JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(path));
            if (stored is null) return null;
            string access = DmdataCredentialProtector.Unprotect(stored.ProtectedAccessToken);
            string refresh = DmdataCredentialProtector.Unprotect(stored.ProtectedRefreshToken);
            return string.IsNullOrWhiteSpace(refresh) ? null : new DmdataOAuthTokens(
                stored.ClientId, access, refresh, stored.Scope, stored.ExpiresAtUtc);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(DmdataOAuthTokens tokens, CancellationToken cancellationToken)
    {
        string access = DmdataCredentialProtector.Protect(tokens.AccessToken);
        string refresh = DmdataCredentialProtector.Protect(tokens.RefreshToken);
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new CryptographicException("DMDATA OAuth認証情報をWindows利用者向けに暗号化できませんでした。");
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new StoredSession(
                tokens.ClientId, access, refresh, tokens.Scope, tokens.ExpiresAtUtc)), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record StoredSession(
        string ClientId,
        string ProtectedAccessToken,
        string ProtectedRefreshToken,
        string Scope,
        DateTimeOffset ExpiresAtUtc);
}
