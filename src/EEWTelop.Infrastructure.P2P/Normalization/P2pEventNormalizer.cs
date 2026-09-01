using System.Text.Json;
using EEWTelop.Application.Events;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Dtos;

namespace EEWTelop.Infrastructure.P2P.Normalization;

public sealed class P2pEventNormalizer : IEventNormalizer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 64,
    };

    private readonly IEventSignatureBuilder _signatureBuilder;

    public P2pEventNormalizer(IEventSignatureBuilder signatureBuilder)
    {
        ArgumentNullException.ThrowIfNull(signatureBuilder);
        _signatureBuilder = signatureBuilder;
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (string.IsNullOrWhiteSpace(raw.Provider))
        {
            return Invalid("provider", "Provider name is required.");
        }

        if (string.IsNullOrWhiteSpace(raw.Json))
        {
            return Invalid("json", "Message JSON is empty.");
        }

        try
        {
            P2pBasicDto? envelope = JsonSerializer.Deserialize<P2pBasicDto>(
                raw.Json,
                SerializerOptions);
            if (envelope?.Code is null)
            {
                return Invalid("code", "Information code is missing.");
            }

            return envelope.Code switch
            {
                551 => NormalizeQuake(raw),
                552 => NormalizeTsunami(raw),
                556 => NormalizeEew(raw),
                _ => NormalizeResult.Ignored(new ValidationIssue(
                    "code",
                    $"Unsupported information code {envelope.Code}.",
                    ValidationSeverity.Warning)),
            };
        }
        catch (JsonException exception)
        {
            return Invalid("json", $"Malformed JSON: {exception.Message}");
        }
    }

    private NormalizeResult NormalizeQuake(RawProviderMessage raw)
    {
        P2pQuakeDto dto = DeserializeRequired<P2pQuakeDto>(raw.Json);
        IReadOnlyList<ValidationIssue> issues = P2pDtoValidator.Validate(dto);
        if (HasErrors(issues))
        {
            return new NormalizeResult(NormalizeStatus.Invalid, null, issues);
        }

        P2pIssueDto issueDto = dto.Issue!;
        P2pQuakeEarthquakeDto earthquakeDto = dto.Earthquake!;
        DateTimeOffset issuedAt = ParseRequired(issueDto.Time);
        DateTimeOffset originTime = ParseRequired(earthquakeDto.Time);
        CorrectionType correction = P2pEnumMapper.ToCorrectionType(issueDto.Correct);
        var issue = new IssueInfo(
            issueDto.Source?.Trim() ?? string.Empty,
            issuedAt,
            issueDto.Type!,
            correction);
        var earthquake = new EarthquakeInfo(
            originTime,
            ArrivalTime: null,
            NormalizeHypocenter(earthquakeDto.Hypocenter),
            P2pEnumMapper.ToScale(earthquakeDto.MaximumScale),
            P2pEnumMapper.ToDomesticTsunami(earthquakeDto.DomesticTsunami),
            P2pEnumMapper.ToForeignTsunami(earthquakeDto.ForeignTsunami));
        QuakePoint[] points = (dto.Points ?? [])
            .Select(static point => new QuakePoint(
                point.Prefecture!.Trim(),
                point.Address!.Trim(),
                point.IsArea!.Value,
                P2pEnumMapper.ToScale(point.Scale),
                PlaceNormalizer.BuildDisplayName(
                    point.Prefecture,
                    point.Address,
                    point.IsArea.Value)))
            .ToArray();

        var disasterEvent = new QuakeEvent(
            EventId.Create(dto.EffectiveId!),
            raw.Provider.Trim(),
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            P2pEnumMapper.ToQuakeIssueType(issueDto.Type),
            earthquake,
            points,
            dto.Comments?.FreeFormComment?.Trim() ?? string.Empty);

        disasterEvent = disasterEvent with { Signature = _signatureBuilder.Build(disasterEvent) };
        return NormalizeResult.Success(disasterEvent, issues);
    }

    private NormalizeResult NormalizeTsunami(RawProviderMessage raw)
    {
        P2pTsunamiDto dto = DeserializeRequired<P2pTsunamiDto>(raw.Json);
        IReadOnlyList<ValidationIssue> issues = P2pDtoValidator.Validate(dto);
        if (HasErrors(issues))
        {
            return new NormalizeResult(NormalizeStatus.Invalid, null, issues);
        }

        P2pIssueDto issueDto = dto.Issue!;
        DateTimeOffset issuedAt = ParseRequired(issueDto.Time);
        var issue = new IssueInfo(
            issueDto.Source!.Trim(),
            issuedAt,
            issueDto.Type!,
            CorrectionType.None);
        TsunamiArea[] areas = (dto.Areas ?? [])
            .Select(static area => new TsunamiArea(
                P2pEnumMapper.ToTsunamiGrade(area.Grade),
                area.Immediate ?? false,
                area.Name!.Trim(),
                area.FirstHeight is null
                    ? null
                    : new TsunamiFirstHeight(
                        ParseOptional(area.FirstHeight.ArrivalTime),
                        area.FirstHeight.Condition?.Trim() ?? string.Empty),
                area.MaximumHeight is null
                    ? null
                    : new TsunamiMaximumHeight(
                        area.MaximumHeight.Description?.Trim() ?? string.Empty,
                        NormalizePositive(area.MaximumHeight.Value))))
            .ToArray();

        var disasterEvent = new TsunamiEvent(
            EventId.Create(dto.EffectiveId!),
            raw.Provider.Trim(),
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            areas,
            dto.Cancelled!.Value,
            expireAt: null);

        disasterEvent = disasterEvent with { Signature = _signatureBuilder.Build(disasterEvent) };
        return NormalizeResult.Success(disasterEvent, issues);
    }

    private NormalizeResult NormalizeEew(RawProviderMessage raw)
    {
        P2pEewDto dto = DeserializeRequired<P2pEewDto>(raw.Json);
        IReadOnlyList<ValidationIssue> issues = P2pDtoValidator.Validate(dto);
        if (HasErrors(issues))
        {
            return new NormalizeResult(NormalizeStatus.Invalid, null, issues);
        }

        P2pEewIssueDto issueDto = dto.Issue!;
        DateTimeOffset issuedAt = ParseRequired(issueDto.Time);
        var issue = new IssueInfo(
            Source: string.Empty,
            issuedAt,
            RawType: "EEW",
            CorrectionType.None,
            issueDto.Serial);
        EarthquakeInfo? earthquake = NormalizeEewEarthquake(dto.Earthquake);
        EewArea[] areas = (dto.Areas ?? [])
            .Select(static area => new EewArea(
                area.Prefecture!.Trim(),
                area.Name!.Trim(),
                P2pEnumMapper.ToScale(area.ScaleFrom),
                P2pEnumMapper.ToScaleUpperBound(area.ScaleTo),
                P2pEnumMapper.ToEewWarningKind(area.KindCode),
                ParseOptional(area.ArrivalTime)))
            .ToArray();

        var disasterEvent = new EewEvent(
            EventId.Create(issueDto.EventId!),
            raw.Provider.Trim(),
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            earthquake,
            areas,
            isWarning: true,
            isFinal: false,
            dto.Cancelled!.Value,
            dto.Test ?? false);

        disasterEvent = disasterEvent with { Signature = _signatureBuilder.Build(disasterEvent) };
        return NormalizeResult.Success(disasterEvent, issues);
    }

    private static T DeserializeRequired<T>(string json)
        where T : class => JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new JsonException("JSON object was null.");

    private static bool HasErrors(IEnumerable<ValidationIssue> issues) =>
        issues.Any(static issue => issue.Severity == ValidationSeverity.Error);

    private static DateTimeOffset ParseRequired(string? value)
    {
        _ = P2pDateTimeParser.TryParse(value, out DateTimeOffset result);
        return result;
    }

    private static DateTimeOffset? ParseOptional(string? value) =>
        P2pDateTimeParser.TryParse(value, out DateTimeOffset result) ? result : null;

    private static HypocenterInfo? NormalizeHypocenter(P2pHypocenterDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        return new HypocenterInfo(
            dto.Name?.Trim() ?? string.Empty,
            dto.ReducedName?.Trim() ?? string.Empty,
            NormalizeCoordinate(dto.Latitude),
            NormalizeCoordinate(dto.Longitude),
            NormalizeDepth(dto.Depth),
            NormalizePositive(dto.Magnitude),
            Condition: string.Empty);
    }

    private static EarthquakeInfo? NormalizeEewEarthquake(P2pEewEarthquakeDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        return new EarthquakeInfo(
            ParseRequired(dto.OriginTime),
            ParseOptional(dto.ArrivalTime),
            NormalizeHypocenter(dto.Hypocenter) is { } hypocenter
                ? hypocenter with { Condition = dto.Condition?.Trim() ?? string.Empty }
                : null,
            JmaScale.Unknown,
            DomesticTsunami.Unknown,
            ForeignTsunami.Unknown);
    }

    private static double? NormalizeCoordinate(double? value) =>
        value is double coordinate && double.IsFinite(coordinate) && coordinate > -200
            ? coordinate
            : null;

    private static double? NormalizePositive(double? value) =>
        value is double number && double.IsFinite(number) && number > 0 ? number : null;

    private static int? NormalizeDepth(double? value) =>
        value is double depth && double.IsFinite(depth) && depth >= 0 && depth <= int.MaxValue
            ? checked((int)Math.Truncate(depth))
            : null;

    private static NormalizeResult Invalid(string path, string message) =>
        NormalizeResult.Invalid(new ValidationIssue(path, message, ValidationSeverity.Error));
}
