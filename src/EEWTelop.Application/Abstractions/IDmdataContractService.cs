using EEWTelop.Application.Configuration;

namespace EEWTelop.Application.Abstractions;

public sealed record DmdataContractInfo(
    string? ContractId,
    int PlanId,
    string PlanName,
    string Classification,
    int DailyPriceYen,
    int MonthlyMaximumPriceYen,
    DateTimeOffset? StartedAt,
    bool IsValid,
    int AdditionalConnectionCount);

public sealed record DmdataContractSnapshot(
    IReadOnlyList<DmdataContractInfo> Items,
    DateTimeOffset RetrievedAtUtc);

public interface IDmdataContractService
{
    Task<DmdataContractSnapshot> GetAsync(
        ProviderSettings settings,
        CancellationToken cancellationToken);
}
