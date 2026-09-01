namespace EEWTelop.Application.Abstractions;

public interface IAxisTokenRefreshService
{
    ValueTask<AxisTokenRefreshResult> RefreshIfDueAsync(
        Uri apiBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default);
}

public sealed record AxisTokenRefreshResult(
    AxisTokenRefreshOutcome Outcome,
    string AccessToken,
    DateTimeOffset? ExpiresAtUtc = null);

public enum AxisTokenRefreshOutcome
{
    NotDue = 0,
    Refreshed = 1,
    Unchanged = 2,
    InvalidToken = 3,
    Expired = 4,
    ContractExpired = 5,
    AuthorizationFailed = 6,
}
