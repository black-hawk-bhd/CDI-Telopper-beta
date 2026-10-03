namespace EEWTelop.Application.Configuration;

public static class DmdataOAuthDefaults
{
    // Public native-app identifier, not a secret or a shared user credential.
    // Each user still authorizes their own account and receives separate tokens.
    public const string ClientId = "CId.uzdSVBCQXiQ0uNIyB5sPXhHxXV0P34wSqbZpImi5SemD";

    public static string ResolveClientId(string? configuredClientId) =>
        string.IsNullOrWhiteSpace(configuredClientId) ? ClientId : configuredClientId.Trim();
}
