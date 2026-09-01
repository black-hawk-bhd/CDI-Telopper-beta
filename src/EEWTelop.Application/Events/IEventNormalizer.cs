using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public interface IEventNormalizer
{
    NormalizeResult Normalize(RawProviderMessage raw);
}

public sealed record RawProviderMessage(
    string Provider,
    string Json,
    SourceMode SourceMode,
    DateTimeOffset ReceivedAt)
{
    // Json is retained for source compatibility with the original P2P pipeline.
    // New providers should consume Payload together with ContentFormat.
    public string Payload => Json;

    public RawProviderContentFormat ContentFormat { get; init; } =
        RawProviderContentFormat.Json;

    // Some transports (notably AXIS) wrap JMA XML-derived content in JSON.
    // Keep the exact transport frame for diagnostics while Payload remains the
    // normalized provider payload consumed by the event normalizer.
    public string? TransportPayload { get; init; }

    public RawProviderContentFormat? TransportContentFormat { get; init; }
}

public enum RawProviderContentFormat
{
    Json = 0,
    JmaXml = 1,
}

public interface IRawProviderMessageArchive
{
    void Configure(EEWTelop.Application.Configuration.LogSettings settings);

    ValueTask SaveAsync(
        RawProviderMessage message,
        CancellationToken cancellationToken = default);
}

public enum NormalizeStatus
{
    Success = 0,
    Ignored,
    Invalid,
}

public enum ValidationSeverity
{
    Warning = 0,
    Error,
}

public sealed record ValidationIssue(
    string Path,
    string Message,
    ValidationSeverity Severity);

public sealed record NormalizeResult(
    NormalizeStatus Status,
    DisasterEvent? Event,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsSuccess => Status == NormalizeStatus.Success && Event is not null;

    public static NormalizeResult Success(
        DisasterEvent disasterEvent,
        IReadOnlyList<ValidationIssue>? issues = null) =>
        new(NormalizeStatus.Success, disasterEvent, issues ?? []);

    public static NormalizeResult Invalid(params ValidationIssue[] issues) =>
        new(NormalizeStatus.Invalid, null, issues);

    public static NormalizeResult Ignored(params ValidationIssue[] issues) =>
        new(NormalizeStatus.Ignored, null, issues);
}

public interface IEventSignatureBuilder
{
    string Build(DisasterEvent disasterEvent);
}
