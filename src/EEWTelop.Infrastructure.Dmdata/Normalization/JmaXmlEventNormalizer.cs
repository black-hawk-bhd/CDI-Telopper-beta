using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EEWTelop.Application.Events;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Dmdata.Normalization;

public sealed partial class JmaXmlEventNormalizer : IEventNormalizer
{
    private readonly IEventSignatureBuilder _signatureBuilder;

    public JmaXmlEventNormalizer(IEventSignatureBuilder signatureBuilder)
    {
        ArgumentNullException.ThrowIfNull(signatureBuilder);
        _signatureBuilder = signatureBuilder;
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!string.Equals(raw.Provider, "dmdata.jp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(raw.Provider, "nii-jma-xml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(raw.Provider, "local-jma-xml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(raw.Provider, "test-library-jma-xml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(raw.Provider, "axis", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeResult.Ignored(new ValidationIssue(
                "provider",
                $"JMA XML normalizer does not handle provider '{raw.Provider}'.",
                ValidationSeverity.Warning));
        }

        if (raw.ContentFormat != RawProviderContentFormat.JmaXml)
        {
            return Invalid("format", "The disaster payload must be raw JMA XML.");
        }

        if (string.IsNullOrWhiteSpace(raw.Payload))
        {
            return Invalid("xml", "XML telegram is empty.");
        }

        try
        {
            XDocument document = LoadSafe(raw.Payload);
            string telegramType = DetectTelegramType(document);
            return telegramType switch
            {
                "VXSE43" or "VXSE45" => NormalizeEew(raw, document, telegramType),
                "VXSE51" or "VXSE52" or "VXSE53" or "VXSE62" or
                    "VYSE50" or "VYSE60" =>
                    NormalizeQuake(raw, document, telegramType),
                "VTSE41" or "VTSE51" or "VTSE52" =>
                    NormalizeTsunami(raw, document, telegramType),
                "VFVO50" or "VFVO56" =>
                    NormalizeVolcano(raw, document, telegramType),
                "VPWW53" or "VPWW54" or "VPWW55" or "VPWW56" or
                "VPWW57" or "VPWW58" or "VPWW59" or "VPWW60" or
                    "VPWW61" or "VPWS50" =>
                    NormalizeWeatherWarning(raw, document, telegramType),
                "VPOA50" => NormalizeWeatherBulletin(
                    raw,
                    document,
                    telegramType,
                    WeatherInformationType.RecordShortDurationHeavyRain,
                    "記録的短時間大雨情報",
                    WeatherWarningLevel.Warning),
                "VPBS50" or "VPBS51" => NormalizeWeatherBulletin(
                    raw,
                    document,
                    telegramType,
                    WeatherInformationType.DisasterPreventionBulletin,
                    telegramType == "VPBS51"
                        ? "気象防災速報（潮位）"
                        : "気象防災速報",
                    WeatherWarningLevel.Warning),
                "VPHW50" or "VPHW51" => NormalizeWeatherBulletin(
                    raw,
                    document,
                    telegramType,
                    WeatherInformationType.TornadoAdvisory,
                    "竜巻注意情報",
                    WeatherWarningLevel.Advisory),
                // jmx-meteorology also carries many valid JMA telegrams that are
                // outside CDI-Telopper's display scope (for example VPFJ50).  They
                // are expected input, not malformed data, so ignore them without
                // producing a user-facing warning on every received frame.
                _ => NormalizeResult.Ignored(),
            };
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or
            FormatException or ArgumentException)
        {
            return Invalid("xml", $"Invalid JMA XML telegram: {exception.Message}");
        }
    }

    private NormalizeResult NormalizeVolcano(
        RawProviderMessage raw,
        XDocument document,
        string telegramType)
    {
        XElement reportHead = RequiredDescendant(document, "Head");
        DateTimeOffset issuedAt = ReadDateTime(reportHead, "ReportDateTime") ?? raw.ReceivedAt;
        VolcanoInformationType informationType = telegramType == "VFVO56"
            ? VolcanoInformationType.EruptionFlash
            : VolcanoInformationType.WarningForecast;

        XElement? body = Descendant(document, "Body");
        XElement[] volcanoInfos = body is null
            ? []
            : Descendants(body, "VolcanoInfo").ToArray();
        XElement? volcanoItem = volcanoInfos
            .Where(info => informationType == VolcanoInformationType.EruptionFlash
                ? string.Equals(
                    AttributeText(info, "type"),
                    "噴火速報",
                    StringComparison.Ordinal)
                : AttributeText(info, "type").Contains("対象火山", StringComparison.Ordinal))
            .SelectMany(static info => Descendants(info, "Item"))
            .FirstOrDefault();
        volcanoItem ??= volcanoInfos
            .Where(info => !AttributeText(info, "type")
                .Contains("市町村", StringComparison.Ordinal))
            .SelectMany(static info => Descendants(info, "Item"))
            .FirstOrDefault();

        XElement? volcanoArea = volcanoItem is null
            ? null
            : Descendants(volcanoItem, "Area").FirstOrDefault();
        string volcanoName = Text(volcanoArea is null ? null : Child(volcanoArea, "Name"));
        string volcanoCode = Text(volcanoArea is null ? null : Child(volcanoArea, "Code"));
        if (string.IsNullOrWhiteSpace(volcanoName))
        {
            volcanoName = ReadVolcanoNameFromTitle(Text(Child(reportHead, "Title")));
        }

        XElement? kind = volcanoItem is null
            ? null
            : Descendants(volcanoItem, "Kind").FirstOrDefault();
        string alertLevelText = Text(kind is null ? null : Child(kind, "Name"));
        string alertLevelCode = Text(kind is null ? null : Child(kind, "Code"));
        string alertCondition = Text(kind is null ? null : Child(kind, "Condition"));
        XElement? previousKind = volcanoItem is null
            ? null
            : Descendants(volcanoItem, "LastKind").FirstOrDefault();
        string previousAlertLevelText = Text(
            previousKind is null ? null : Child(previousKind, "Name"));
        string previousAlertLevelCode = Text(
            previousKind is null ? null : Child(previousKind, "Code"));
        VolcanoAlertLevel alertLevel = ReadVolcanoAlertLevel(alertLevelText);
        VolcanoTargetArea[] targetAreas = volcanoInfos
            .Where(info =>
                AttributeText(info, "type").Contains("対象市町村", StringComparison.Ordinal) &&
                !AttributeText(info, "type").Contains("防災対応", StringComparison.Ordinal))
            .SelectMany(static info => Descendants(info, "Item"))
            .SelectMany(ReadVolcanoTargetAreas)
            .DistinctBy(static area => (
                area.Code,
                area.Name,
                area.KindCode,
                area.KindName,
                area.Status,
                area.PreviousKindCode))
            .ToArray();
        if (targetAreas.Length == 0)
        {
            targetAreas = volcanoInfos
                .Where(info => AttributeText(info, "type")
                    .Contains("市町村", StringComparison.Ordinal) &&
                    !AttributeText(info, "type").Contains("防災対応", StringComparison.Ordinal))
                .SelectMany(static info => Descendants(info, "Item"))
                .SelectMany(ReadVolcanoTargetAreas)
                .DistinctBy(static area => (
                    area.Code,
                    area.Name,
                    area.KindCode,
                    area.KindName,
                    area.Status,
                    area.PreviousKindCode))
                .ToArray();
        }

        XElement? content = body is null ? null : Child(body, "VolcanoInfoContent");
        XElement? reportHeadline = Child(reportHead, "Headline");
        string headTitle = Text(Child(reportHead, "Title"));
        string notice = Text(body is null ? null : Child(body, "Notice"));
        string contentText = Text(content is null ? null : Child(content, "Text"));
        string bodyText = Text(body is null ? null : Child(body, "Text"));
        string infoType = Text(Descendant(reportHead, "InfoType"));
        bool telegramCancelled = infoType.Contains("取消", StringComparison.Ordinal);
        string headline = telegramCancelled
            ? FirstNonBlank(
                bodyText,
                contentText,
                Text(reportHeadline is null ? null : Child(reportHeadline, "Text")),
                headTitle)
            : FirstNonBlank(
                Text(content is null ? null : Child(content, "VolcanoHeadline")),
                Text(reportHeadline is null ? null : Child(reportHeadline, "Text")),
                headTitle);
        string activity = Text(content is null ? null : Child(content, "VolcanoActivity"));
        string prevention = Text(content is null ? null : Child(content, "VolcanoPrevention"));
        string otherInfo = Text(content is null ? null : Child(content, "OtherInfo"));
        string appendix = Text(content is null ? null : Child(content, "Appendix"));
        XElement? eventDateTime = informationType == VolcanoInformationType.EruptionFlash &&
            volcanoItem is not null
                ? Descendant(volcanoItem, "EventDateTime")
                : null;
        DateTimeOffset? eventTime = eventDateTime is null || volcanoItem is null
            ? null
            : ReadDateTime(volcanoItem, "EventDateTime");
        string eventTimePrecision = eventDateTime is null
            ? string.Empty
            : AttributeText(eventDateTime, "significant");
        bool eventTimeIsApproximate = eventDateTime is not null &&
            AttributeText(eventDateTime, "dubious").Contains('頃');
        bool allTargetAreasReleased = targetAreas.Length > 0 &&
            targetAreas.All(static area =>
                area.Status.Contains("解除", StringComparison.Ordinal) ||
                area.KindName.Contains("警報解除", StringComparison.Ordinal));
        bool warningReleased = !telegramCancelled &&
            (headTitle.Contains("警報解除", StringComparison.Ordinal) ||
             headline.Contains("警報解除", StringComparison.Ordinal) ||
             allTargetAreasReleased);
        bool isWarning = !telegramCancelled && !warningReleased &&
            (alertLevel >= VolcanoAlertLevel.Level2 ||
             headTitle.Contains("噴火警報", StringComparison.Ordinal) ||
             headTitle.Contains("火口周辺警報", StringComparison.Ordinal) ||
             targetAreas.Any(static area =>
                 area.KindName.Contains("警報", StringComparison.Ordinal) &&
                 !area.KindName.Contains("解除", StringComparison.Ordinal)));
        bool cancelled = telegramCancelled || warningReleased;
        var issue = new IssueInfo(
            Text(Descendant(document, "PublishingOffice")),
            issuedAt,
            telegramType,
            ReadCorrection(document),
            Text(Descendant(reportHead, "Serial")),
            infoType);
        var disasterEvent = new VolcanoEvent(
            ReadEventId(reportHead, telegramType, issuedAt),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            informationType,
            volcanoName,
            volcanoCode,
            alertLevel,
            alertLevelText,
            NormalizeVolcanoText(headline),
            NormalizeVolcanoText(activity),
            NormalizeVolcanoText(prevention),
            targetAreas,
            eventTime,
            cancelled,
            isWarning,
            alertLevelCode,
            alertCondition,
            previousAlertLevelText,
            previousAlertLevelCode,
            eventTimeIsApproximate,
            eventTimePrecision,
            telegramCancelled,
            NormalizeVolcanoText(notice),
            NormalizeVolcanoText(otherInfo),
            NormalizeVolcanoText(appendix),
            NormalizeVolcanoText(contentText),
            NormalizeVolcanoText(bodyText));
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        return NormalizeResult.Success(disasterEvent);
    }

    private static IEnumerable<VolcanoTargetArea> ReadVolcanoTargetAreas(XElement item)
    {
        XElement? kind = Descendants(item, "Kind").FirstOrDefault();
        string kindName = Text(kind is null ? null : Child(kind, "Name"));
        string kindCode = Text(kind is null ? null : Child(kind, "Code"));
        string status = Text(kind is null ? null : Child(kind, "Condition"));
        XElement? previousKind = Descendants(item, "LastKind").FirstOrDefault();
        string previousKindName = Text(
            previousKind is null ? null : Child(previousKind, "Name"));
        string previousKindCode = Text(
            previousKind is null ? null : Child(previousKind, "Code"));
        return Descendants(item, "Area")
            .Select(area => new VolcanoTargetArea(
                Text(Child(area, "Name")),
                Text(Child(area, "Code")),
                kindName,
                kindCode,
                status,
                previousKindName,
                previousKindCode))
            .Where(static area => !string.IsNullOrWhiteSpace(area.Name));
    }

    private static VolcanoAlertLevel ReadVolcanoAlertLevel(string value)
    {
        string normalized = value
            .Replace('１', '1')
            .Replace('２', '2')
            .Replace('３', '3')
            .Replace('４', '4')
            .Replace('５', '5');
        Match match = Regex.Match(
            normalized,
            @"(?:レベル|LEVEL)\s*([1-5])",
            RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int level)
            ? (VolcanoAlertLevel)level
            : VolcanoAlertLevel.Unknown;
    }

    private static string ReadVolcanoNameFromTitle(string title)
    {
        string normalized = Regex.Replace(title, @"\s+", " ").Trim();
        Match match = Regex.Match(normalized, @"火山名\s+(.+?)\s+(?:噴火|火口周辺|警報|予報)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string NormalizeVolcanoText(string value) => Regex.Replace(
        value.Replace('\r', '\n'),
        @"[ \t　]+",
        " ").Trim();

    private static string FirstNonBlank(params string[] values) => values
        .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private NormalizeResult NormalizeQuake(
        RawProviderMessage raw,
        XDocument document,
        string telegramType)
    {
        XElement reportHead = RequiredDescendant(document, "Head");
        DateTimeOffset issuedAt = ReadDateTime(reportHead, "ReportDateTime") ?? raw.ReceivedAt;
        XElement? earthquakeElement = Descendant(document, "Earthquake");
        DateTimeOffset originTime = earthquakeElement is null
            ? ReadDateTime(reportHead, "TargetDateTime") ?? issuedAt
            : ReadDateTime(earthquakeElement, "OriginTime") ?? issuedAt;
        HypocenterInfo? hypocenter = earthquakeElement is null
            ? null
            : ReadHypocenter(earthquakeElement);
        QuakePoint[] points = ReadQuakePoints(document);
        XElement? observation = Descendant(document, "Observation");
        JmaScale maximumScale = ReadScale(Text(
            observation is null ? null : Child(observation, "MaxInt")));
        if (maximumScale == JmaScale.Unknown && points.Length > 0)
        {
            maximumScale = points.Max(static point => point.Scale);
        }

        XElement? forecastCommentElement = Descendant(document, "ForecastComment");
        string forecastComment = ReadCommentText(forecastCommentElement);
        DomesticTsunami domesticTsunami = ReadDomesticTsunami(forecastComment);
        LongPeriodIntensityInfo? longPeriodIntensity = telegramType == "VXSE62"
            ? ReadLongPeriodIntensity(document)
            : null;
        string infoType = Text(Descendant(reportHead, "InfoType"));
        string headline = ReadHeadlineText(reportHead);
        if (telegramType == "VYSE50")
        {
            headline = MergeDisplayComments(
                Text(Descendant(reportHead, "Title")),
                headline);
        }
        bool cancelled = infoType.Contains("取消", StringComparison.Ordinal) ||
            headline.Contains("取消", StringComparison.Ordinal);
        var issue = new IssueInfo(
            Text(Descendant(document, "PublishingOffice")),
            issuedAt,
            telegramType,
            ReadCorrection(document),
            Text(Descendant(reportHead, "Serial")),
            infoType);
        var earthquake = new EarthquakeInfo(
            originTime,
            ArrivalTime: null,
            hypocenter,
            maximumScale,
            domesticTsunami,
            ForeignTsunami.Unknown);
        var disasterEvent = new QuakeEvent(
            ReadEventId(reportHead, telegramType, issuedAt),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            telegramType switch
            {
                "VXSE51" => QuakeIssueType.ScalePrompt,
                "VXSE52" => QuakeIssueType.Destination,
                "VXSE53" => QuakeIssueType.DetailScale,
                "VXSE62" => QuakeIssueType.LongPeriodObservation,
                "VYSE50" => QuakeIssueType.NankaiTroughTemporaryInformation,
                "VYSE60" => QuakeIssueType.SubsequentEarthquakeAdvisory,
                _ => QuakeIssueType.Other,
            },
            earthquake,
            points,
            telegramType == "VXSE62"
                ? string.Empty
                : MergeDisplayComments(
                    ReadEewNoStrongShakingComment(forecastCommentElement),
                    ReadCommentText(Descendant(document, "FreeFormComment"))),
            longPeriodIntensity,
            cancelled,
            headline);
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        return NormalizeResult.Success(disasterEvent);
    }

    private NormalizeResult NormalizeEew(
        RawProviderMessage raw,
        XDocument document,
        string telegramType)
    {
        XElement reportHead = RequiredDescendant(document, "Head");
        DateTimeOffset issuedAt = ReadDateTime(reportHead, "ReportDateTime") ?? raw.ReceivedAt;
        XElement? earthquakeElement = Descendant(document, "Earthquake");
        EarthquakeInfo? earthquake = earthquakeElement is null
            ? null
            : new EarthquakeInfo(
                ReadDateTime(earthquakeElement, "OriginTime") ?? issuedAt,
                ReadDateTime(earthquakeElement, "ArrivalTime"),
                ReadHypocenter(earthquakeElement),
                JmaScale.Unknown,
                DomesticTsunami.Unknown,
                ForeignTsunami.Unknown);
        string serial = Text(Descendant(reportHead, "Serial"));
        string infoType = Text(Descendant(reportHead, "InfoType"));
        string headline = Text(Descendant(reportHead, "Headline"));
        bool cancelled = infoType.Contains("取消", StringComparison.Ordinal) ||
            headline.Contains("取消", StringComparison.Ordinal);
        bool hasWarning = telegramType == "VXSE43" || HasEewWarning(document);
        if (telegramType == "VXSE45" && !hasWarning && !cancelled)
        {
            return NormalizeResult.Ignored();
        }

        string nextAdvisory = Text(Descendant(document, "NextAdvisory"));
        bool isFinal = nextAdvisory.Contains("最終", StringComparison.Ordinal) ||
            nextAdvisory.Contains("終了", StringComparison.Ordinal) ||
            nextAdvisory.Contains("もって", StringComparison.Ordinal);
        bool isTest = raw.SourceMode != SourceMode.Production || IsTestTelegram(document);
        var issue = new IssueInfo(
            Text(Descendant(document, "PublishingOffice")),
            issuedAt,
            telegramType,
            CorrectionType.None,
            string.IsNullOrWhiteSpace(serial) ? null : serial,
            infoType);
        var disasterEvent = new EewEvent(
            ReadEventId(reportHead, telegramType, issuedAt),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            earthquake,
            ReadEewAreas(document, warningOnly: telegramType == "VXSE45"),
            isWarning: hasWarning || cancelled,
            isFinal,
            cancelled,
            isTest);
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        return NormalizeResult.Success(disasterEvent);
    }

    private NormalizeResult NormalizeTsunami(
        RawProviderMessage raw,
        XDocument document,
        string telegramType)
    {
        XElement reportHead = RequiredDescendant(document, "Head");
        DateTimeOffset issuedAt = ReadDateTime(reportHead, "ReportDateTime") ?? raw.ReceivedAt;
        string infoType = Text(Descendant(reportHead, "InfoType"));
        string headline = Text(Descendant(reportHead, "Headline"));
        string[] categoryNames = ReadTsunamiCategoryNames(document);
        bool hasActiveWarning = categoryNames.Any(static categoryName =>
            !IsTsunamiReleaseCategory(categoryName) &&
            ReadTsunamiGrade(categoryName) is
                TsunamiGrade.MajorWarning or TsunamiGrade.Warning or TsunamiGrade.Watch);
        bool hasReleaseCategory = categoryNames.Any(IsTsunamiReleaseCategory);
        bool cancelled = infoType.Contains("取消", StringComparison.Ordinal) ||
            headline.Contains("全て解除", StringComparison.Ordinal) ||
            headline.Contains("取り消", StringComparison.Ordinal) ||
            ((hasReleaseCategory || headline.Contains("解除", StringComparison.Ordinal)) &&
                !hasActiveWarning);
        var issue = new IssueInfo(
            Text(Descendant(document, "PublishingOffice")),
            issuedAt,
            telegramType,
            ReadCorrection(document),
            NullIfEmpty(Text(Descendant(reportHead, "Serial"))),
            infoType);
        var disasterEvent = new TsunamiEvent(
            ReadEventId(reportHead, telegramType, issuedAt),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            ReadTsunamiAreas(document, telegramType),
            cancelled,
            ReadDateTime(reportHead, "ValidDateTime"),
            ReadTsunamiObservationAsOf(reportHead, issuedAt, telegramType))
        {
            WarningStateChanged = telegramType == "VTSE41" &&
                HasTsunamiWarningStateChange(document),
        };
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        return NormalizeResult.Success(disasterEvent);
    }

    private NormalizeResult NormalizeWeatherWarning(
        RawProviderMessage raw,
        XDocument document,
        string telegramType)
    {
        XElement reportHead = RequiredDescendant(document, "Head");
        XElement body = RequiredDescendant(document, "Body");
        DateTimeOffset issuedAt = ReadDateTime(reportHead, "ReportDateTime") ?? raw.ReceivedAt;
        WeatherWarningItem[] items = ReadWeatherWarningItems(document);
        string headline = ReadHeadlineText(reportHead);
        string infoType = Text(Descendant(reportHead, "InfoType"));
        bool telegramCancelled = infoType.Contains("取消", StringComparison.Ordinal);
        bool explicitRelease = headline.Contains("解除", StringComparison.Ordinal) ||
            (items.Length == 0 && ContainsOnlyNoWarningStatuses(body));
        if (items.Length == 0 && !explicitRelease && !telegramCancelled)
        {
            return NormalizeResult.Ignored(new ValidationIssue(
                "Body.Warning",
                "Weather warning telegram contained no displayable warning area item.",
                ValidationSeverity.Warning));
        }

        bool cancelled = telegramCancelled || explicitRelease ||
            items.All(static item => !item.IsActive);
        var issue = new IssueInfo(
            Text(Descendant(document, "PublishingOffice")),
            issuedAt,
            telegramType,
            ReadCorrection(document),
            NullIfEmpty(Text(Descendant(reportHead, "Serial"))),
            infoType);
        var disasterEvent = new WeatherWarningEvent(
            ReadWeatherWarningEventId(reportHead, document, telegramType),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            headline,
            items,
            cancelled,
            WeatherInformationType.WarningAndAdvisory,
            ReadDateTime(reportHead, "ValidDateTime"));
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        ValidationIssue[] issues = items
            .Where(static item => item.IsActive &&
                item.Level == WeatherWarningLevel.Unknown)
            .Select(item => new ValidationIssue(
                "Body.Warning.Item.Kind",
                $"未知の気象警報種別を安全側で警報として表示します: {item.KindName}",
                ValidationSeverity.Warning))
            .ToArray();
        return NormalizeResult.Success(disasterEvent, issues);
    }

    private static WeatherWarningItem[] ReadWeatherWarningItems(XDocument document)
    {
        XElement? body = Descendant(document, "Body");
        if (body is null)
        {
            return [];
        }

        XElement[] warningContainers = Descendants(body, "Warning").ToArray();
        if (warningContainers.Length > 0)
        {
            foreach (IGrouping<int, XElement> group in warningContainers
                .GroupBy(WarningAreaRank)
                .OrderByDescending(static group => group.Key))
            {
                WeatherWarningItem[] rankedItems = ParseWeatherWarningItems(
                    group.SelectMany(static warning => Children(warning, "Item")));
                if (rankedItems.Length > 0)
                {
                    return rankedItems;
                }
            }
        }

        XElement[] meteorologicalContainers = Descendants(body, "MeteorologicalInfos")
            .Where(static container => AttributeText(container, "type")
                .Contains("警報", StringComparison.Ordinal))
            .ToArray();
        if (meteorologicalContainers.Length > 0)
        {
            WeatherWarningItem[] meteorologicalItems = ParseWeatherWarningItems(
                meteorologicalContainers.SelectMany(static container => Descendants(container, "Item")));
            if (meteorologicalItems.Length > 0)
            {
                return meteorologicalItems;
            }
        }

        return ParseWeatherWarningItems(Descendants(body, "Item"));
    }

    private NormalizeResult NormalizeWeatherBulletin(
        RawProviderMessage raw,
        XDocument document,
        string telegramType,
        WeatherInformationType informationType,
        string defaultKindName,
        WeatherWarningLevel level)
    {
        XElement reportHead = RequiredDescendant(document, "Head");
        XElement body = RequiredDescendant(document, "Body");
        DateTimeOffset issuedAt = ReadDateTime(reportHead, "ReportDateTime") ?? raw.ReceivedAt;
        string headTitle = Text(Descendant(reportHead, "Title"));
        string controlTitle = Text(Descendant(Descendant(document, "Control"), "Title"));
        string kindName = informationType == WeatherInformationType.DisasterPreventionBulletin &&
            !string.IsNullOrWhiteSpace(headTitle)
                ? headTitle
                : defaultKindName;
        string headline = ReadHeadlineText(reportHead);
        if (string.IsNullOrWhiteSpace(headline))
        {
            headline = Text(Descendant(body, "Text"));
        }

        string infoType = Text(Descendant(reportHead, "InfoType"));
        bool cancelled = infoType.Contains("取消", StringComparison.Ordinal) ||
            headline.Contains("取消", StringComparison.Ordinal) ||
            headline.Contains("解除", StringComparison.Ordinal);
        string status = cancelled
            ? "解除"
            : string.IsNullOrWhiteSpace(infoType) ? "発表" : infoType;
        WeatherWarningItem[] items = ReadWeatherBulletinItems(
            body,
            kindName,
            level,
            status,
            headTitle,
            controlTitle,
            cancelled);
        if (items.Length == 0 && !cancelled)
        {
            return NormalizeResult.Ignored(new ValidationIssue(
                "Body.Area",
                "Weather bulletin contained no displayable target area.",
                ValidationSeverity.Warning));
        }

        var issue = new IssueInfo(
            Text(Descendant(document, "PublishingOffice")),
            issuedAt,
            telegramType,
            ReadCorrection(document),
            NullIfEmpty(Text(Descendant(reportHead, "Serial"))),
            infoType);
        var disasterEvent = new WeatherWarningEvent(
            ReadEventId(reportHead, telegramType, issuedAt),
            raw.Provider,
            issuedAt,
            raw.ReceivedAt,
            signature: string.Empty,
            raw.SourceMode,
            issue,
            headline,
            items,
            cancelled,
            informationType,
            ReadDateTime(reportHead, "ValidDateTime"));
        disasterEvent = disasterEvent with
        {
            Signature = _signatureBuilder.Build(disasterEvent),
        };
        return NormalizeResult.Success(disasterEvent);
    }

    private static WeatherWarningItem[] ReadWeatherBulletinItems(
        XElement body,
        string kindName,
        WeatherWarningLevel level,
        string status,
        string headTitle,
        string controlTitle,
        bool cancelled)
    {
        XElement[] areaElements = Descendants(body, "Areas")
            .SelectMany(static areas => Children(areas, "Area"))
            .ToArray();
        if (areaElements.Length == 0)
        {
            areaElements = Descendants(body, "Area").ToArray();
        }

        WeatherWarningItem[] items = areaElements
            .Select(area => new WeatherWarningItem(
                Text(Child(area, "Name") ?? Descendant(area, "Name")),
                Text(Child(area, "Code") ?? Descendant(area, "Code")),
                kindName,
                string.Empty,
                level,
                status,
                !cancelled))
            .Where(static item => !string.IsNullOrWhiteSpace(item.AreaName))
            .GroupBy(static item => (item.AreaCode, item.AreaName))
            .Select(static group => group.First())
            .ToArray();
        if (items.Length > 0)
        {
            return items;
        }

        string title = string.IsNullOrWhiteSpace(headTitle) ? controlTitle : headTitle;
        string areaName = ExtractBulletinAreaName(title);
        return string.IsNullOrWhiteSpace(areaName)
            ? []
            :
            [
                new WeatherWarningItem(
                    areaName,
                    string.Empty,
                    kindName,
                    string.Empty,
                    level,
                    status,
                    !cancelled),
            ];
    }

    private static string ExtractBulletinAreaName(string title)
    {
        string[] separators =
        [
            "気象防災速報",
            "記録的短時間大雨情報",
            "竜巻注意情報",
        ];
        foreach (string separator in separators)
        {
            int index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return title[..index].Trim();
            }
        }

        return string.Empty;
    }

    private static WeatherWarningItem[] ParseWeatherWarningItems(
        IEnumerable<XElement> itemElements)
    {
        return itemElements
            .SelectMany(ReadWeatherWarningItem)
            .Where(static item =>
                !string.IsNullOrWhiteSpace(item.AreaName) &&
                !string.IsNullOrWhiteSpace(item.KindName))
            .GroupBy(static item => (
                item.AreaCode,
                item.AreaName,
                item.KindCode,
                item.KindName,
                item.Status))
            .Select(static group => group.First())
            .ToArray();
    }

    private static IEnumerable<WeatherWarningItem> ReadWeatherWarningItem(XElement item)
    {
        XElement[] kinds = Children(item, "Kind").ToArray();
        if (kinds.Length == 0)
        {
            kinds = Descendants(item, "Kind").ToArray();
        }

        XElement[] areaElements = Children(item, "Area").ToArray();
        if (areaElements.Length == 0)
        {
            XElement? areas = Child(item, "Areas") ?? Descendant(item, "Areas");
            areaElements = areas is null
                ? []
                : Children(areas, "Area").ToArray();
        }
        foreach (XElement kind in kinds)
        {
            string kindName = Text(Child(kind, "Name") ?? Descendant(kind, "Name"));
            string kindCode = Text(Child(kind, "Code") ?? Descendant(kind, "Code"));
            string status = Text(Child(kind, "Status") ?? Descendant(kind, "Status"));
            WeatherWarningLevel level = ReadWeatherWarningLevel(kindName);
            bool active = !IsWeatherWarningRelease(status, kindName);
            foreach (XElement area in areaElements)
            {
                yield return new WeatherWarningItem(
                    Text(Child(area, "Name") ?? Descendant(area, "Name")),
                    Text(Child(area, "Code") ?? Descendant(area, "Code")),
                    kindName,
                    kindCode,
                    level,
                    status,
                    active);
            }
        }
    }

    private static int WarningAreaRank(XElement warning)
    {
        string type = AttributeText(warning, "type");
        if (type.Contains("市町村等をまとめた地域", StringComparison.Ordinal))
        {
            return 3;
        }

        if (type.Contains("市町村", StringComparison.Ordinal))
        {
            return 4;
        }

        if (type.Contains("二次細分", StringComparison.Ordinal))
        {
            return 2;
        }

        return type.Contains("府県予報区", StringComparison.Ordinal) ? 1 : 0;
    }

    private static string ReadHeadlineText(XContainer reportHead)
    {
        XElement? headline = Descendant(reportHead, "Headline");
        return headline is null
            ? string.Empty
            : Text(Child(headline, "Text") ?? Descendant(headline, "Text"));
    }

    private static bool ContainsOnlyNoWarningStatuses(XContainer body)
    {
        string[] statuses = Descendants(body, "Kind")
            .Select(static kind => Text(Child(kind, "Status")))
            .Where(static status => !string.IsNullOrWhiteSpace(status))
            .ToArray();
        return statuses.Length > 0 && statuses.All(static status =>
            status.Contains("発表警報・注意報はなし", StringComparison.Ordinal));
    }

    private static WeatherWarningLevel ReadWeatherWarningLevel(string name)
    {
        if (name.Contains("特別警報", StringComparison.Ordinal) ||
            name.Contains("レベル５", StringComparison.Ordinal) ||
            name.Contains("レベル5", StringComparison.Ordinal))
        {
            return WeatherWarningLevel.SpecialWarning;
        }

        if ((name.Contains("警報", StringComparison.Ordinal) ||
             name.Contains("危険警報", StringComparison.Ordinal) ||
             name.Contains("レベル４", StringComparison.Ordinal) ||
             name.Contains("レベル4", StringComparison.Ordinal) ||
             name.Contains("レベル３", StringComparison.Ordinal) ||
             name.Contains("レベル3", StringComparison.Ordinal)) &&
            !name.Contains("注意報", StringComparison.Ordinal))
        {
            return WeatherWarningLevel.Warning;
        }

        return name.Contains("注意報", StringComparison.Ordinal) ||
            name.Contains("レベル２", StringComparison.Ordinal) ||
            name.Contains("レベル2", StringComparison.Ordinal)
            ? WeatherWarningLevel.Advisory
            : WeatherWarningLevel.Unknown;
    }

    private static bool IsWeatherWarningRelease(string status, string name) =>
        status.Contains("解除", StringComparison.Ordinal) ||
        name.Contains("解除", StringComparison.Ordinal) ||
        status.Contains("なし", StringComparison.Ordinal);

    private static EventId ReadWeatherWarningEventId(
        XContainer reportHead,
        XContainer document,
        string telegramType)
    {
        string eventId = Text(Descendant(reportHead, "EventID"));
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            return EventId.Create(eventId);
        }

        string office = Text(Descendant(document, "PublishingOffice"));
        return EventId.Create($"weather-{telegramType}-{office}");
    }

    private static QuakePoint[] ReadQuakePoints(XContainer document)
    {
        XElement? observation = Descendant(document, "Observation");
        if (observation is null)
        {
            return [];
        }

        XElement[] candidates = Descendants(observation, "IntensityStation").ToArray();
        bool isArea = false;
        if (candidates.Length == 0)
        {
            candidates = Descendants(observation, "City").ToArray();
        }

        if (candidates.Length == 0)
        {
            candidates = Descendants(observation, "Area").ToArray();
            isArea = true;
        }

        return candidates
            .Select(element =>
            {
                string address = Text(Descendant(element, "Name"));
                string prefecture = element.Ancestors()
                    .FirstOrDefault(static ancestor => ancestor.Name.LocalName == "Pref") is { } pref
                        ? Text(Descendant(pref, "Name"))
                        : string.Empty;
                JmaScale scale = ReadScale(Text(Descendant(element, "Int")));
                if (scale == JmaScale.Unknown)
                {
                    scale = ReadScale(Text(Descendant(element, "MaxInt")));
                }

                return new QuakePoint(
                    prefecture,
                    address,
                    isArea,
                    scale,
                    PlaceNormalizer.BuildDisplayName(prefecture, address, isArea));
            })
            .Where(static point => !string.IsNullOrWhiteSpace(point.Address) &&
                point.Scale != JmaScale.Unknown)
            .GroupBy(static point => (point.DisplayName, point.Scale))
            .Select(static group => group.First())
            .ToArray();
    }

    private static LongPeriodIntensityInfo? ReadLongPeriodIntensity(XContainer document)
    {
        XElement? observation = Descendant(document, "Observation");
        if (observation is null)
        {
            return null;
        }

        var areas = new List<LongPeriodIntensityArea>();
        foreach (XElement pref in Descendants(observation, "Pref"))
        {
            string prefecture = Text(Child(pref, "Name"));
            foreach (XElement area in Children(pref, "Area"))
            {
                string areaName = Text(Child(area, "Name"));
                int areaClass = ReadLongPeriodClass(Text(Child(area, "MaxLgInt")));
                if (areaClass == 0)
                {
                    areaClass = Descendants(area, "LgInt")
                        .Select(static element => ReadLongPeriodClass(Text(element)))
                        .DefaultIfEmpty(0)
                        .Max();
                }

                if (!string.IsNullOrWhiteSpace(areaName) && areaClass > 0)
                {
                    areas.Add(new LongPeriodIntensityArea(prefecture, areaName, areaClass));
                }
            }
        }

        LongPeriodIntensityArea[] distinctAreas = areas
            .GroupBy(static area => (area.Prefecture, area.Area))
            .Select(static group => group.OrderByDescending(area => area.Class).First())
            .ToArray();
        int maximumClass = ReadLongPeriodClass(Text(Child(observation, "MaxLgInt")));
        if (maximumClass == 0 && distinctAreas.Length > 0)
        {
            maximumClass = distinctAreas.Max(static area => area.Class);
        }

        return maximumClass == 0 && distinctAreas.Length == 0
            ? null
            : new LongPeriodIntensityInfo(maximumClass, distinctAreas);
    }

    private static int ReadLongPeriodClass(string value) =>
        int.TryParse(
            value.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int result) && result is >= 1 and <= 4
                ? result
                : 0;

    private static string ReadCommentText(XElement? comment)
    {
        if (comment is null)
        {
            return string.Empty;
        }

        string[] textNodes = Children(comment, "Text")
            .Select(Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return textNodes.Length == 0
            ? Text(comment)
            : string.Join(Environment.NewLine, textNodes);
    }

    private static string ReadEewNoStrongShakingComment(XElement? forecastComment)
    {
        if (forecastComment is null)
        {
            return string.Empty;
        }

        string text = ReadCommentText(forecastComment);
        string[] codes = Text(Child(forecastComment, "Code"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isCode0245 = codes.Contains("0245", StringComparer.Ordinal);
        bool hasEquivalentText =
            text.Contains("緊急地震速報を発表しましたが", StringComparison.Ordinal) &&
            text.Contains("強い揺れは観測されませんでした", StringComparison.Ordinal);
        return isCode0245 || hasEquivalentText
            ? "この地震で緊急地震速報を発表しましたが、強い揺れは観測されませんでした。"
            : string.Empty;
    }

    private static string MergeDisplayComments(params string[] comments) => string.Join(
        Environment.NewLine,
        comments
            .Where(static comment => !string.IsNullOrWhiteSpace(comment))
            .Select(static comment => comment.Trim())
            .Distinct(StringComparer.Ordinal));

    private static bool HasEewWarning(XContainer document) =>
        Descendants(document, "Kind").Any(kind =>
        {
            string name = Text(Descendant(kind, "Name"));
            string code = Text(Descendant(kind, "Code"));
            return name.Contains("緊急地震速報（警報）", StringComparison.Ordinal) ||
                code is "10" or "11" or "19" or "31";
        });

    private static EewArea[] ReadEewAreas(XContainer document, bool warningOnly = false)
    {
        XElement? forecast = Descendant(document, "Forecast");
        if (forecast is null)
        {
            return [];
        }

        return Descendants(forecast, "Area")
            .Select(area =>
            {
                XElement? forecastInt = Descendant(area, "ForecastInt");
                string from = Text(forecastInt is null ? null : Descendant(forecastInt, "From"));
                string to = Text(forecastInt is null ? null : Descendant(forecastInt, "To"));
                XElement? kind = Descendant(area, "Kind");
                string kindCode = Text(kind is null ? null : Descendant(kind, "Code"));
                string prefecture = area.Ancestors()
                    .FirstOrDefault(static ancestor => ancestor.Name.LocalName == "Pref") is { } pref
                        ? Text(Descendant(pref, "Name"))
                        : string.Empty;
                return new EewArea(
                    prefecture,
                    Text(Descendant(area, "Name")),
                    ReadScale(from),
                    (int)ReadScale(to),
                    kindCode switch
                    {
                        "10" => EewWarningKind.ForecastNotArrived,
                        "11" => EewWarningKind.ForecastArrived,
                        "19" => EewWarningKind.Plum,
                        _ => EewWarningKind.Unknown,
                    },
                    ReadDateTime(area, "ArrivalTime"));
            })
            .Where(area => !string.IsNullOrWhiteSpace(area.Name) &&
                (!warningOnly || area.WarningKind != EewWarningKind.Unknown))
            .ToArray();
    }

    private static TsunamiArea[] ReadTsunamiAreas(
        XContainer document,
        string telegramType)
    {
        var areas = new List<TsunamiArea>();
        XElement? forecast = Descendant(document, "Forecast");
        if (forecast is not null)
        {
            foreach (XElement item in Children(forecast, "Item"))
            {
                string categoryName = Text(Descendant(Descendant(item, "Category"), "Name"));
                if (IsTsunamiReleaseCategory(categoryName))
                {
                    continue;
                }

                XElement? area = Child(item, "Area");
                string areaName = area is null ? string.Empty : Text(Descendant(area, "Name"));
                TsunamiArea forecastArea = ReadTsunamiArea(item, categoryName, areaName) with
                {
                    Role = TsunamiInformationRole.ForecastArea,
                };
                if (!string.IsNullOrWhiteSpace(forecastArea.Name))
                {
                    areas.Add(forecastArea);
                }

                if (telegramType == "VTSE51")
                {
                    areas.AddRange(Children(item, "Station")
                        .Select(station => ReadTsunamiArea(station, categoryName) with
                        {
                            Role = TsunamiInformationRole.StationForecast,
                            ParentAreaName = areaName,
                            HighTideAt = ReadDateTime(station, "HighTideDateTime"),
                        })
                        .Where(static station => !string.IsNullOrWhiteSpace(station.Name)));
                }
            }
        }

        XElement? observation = Descendant(document, "Observation");
        if (observation is not null)
        {
            TsunamiInformationRole role = telegramType == "VTSE52"
                ? TsunamiInformationRole.OffshoreObservation
                : TsunamiInformationRole.CoastalObservation;
            foreach (XElement item in Children(observation, "Item"))
            {
                string categoryName = Text(Descendant(Descendant(item, "Category"), "Name"));
                XElement? area = Child(item, "Area");
                string areaName = area is null ? string.Empty : Text(Descendant(area, "Name"));
                areas.AddRange(Children(item, "Station")
                    .Select(station => ReadTsunamiArea(station, categoryName) with
                    {
                        Role = role,
                        ParentAreaName = areaName,
                    })
                    .Where(static station => !string.IsNullOrWhiteSpace(station.Name)));
            }
        }

        return areas.ToArray();
    }

    private static TsunamiArea ReadTsunamiArea(
        XElement source,
        string categoryName,
        string? areaName = null)
    {
        XElement? firstHeight = Descendant(source, "FirstHeight");
        string condition = Text(Descendant(firstHeight, "Condition"));
        if (string.IsNullOrWhiteSpace(condition))
        {
            condition = Text(Descendant(firstHeight, "Initial"));
        }

        XElement? maxHeight = Descendant(source, "MaxHeight");
        XElement? heightElement = Descendant(maxHeight, "TsunamiHeight");
        string heightDescription = heightElement?.Attributes()
            .FirstOrDefault(static attribute =>
                attribute.Name.LocalName.Equals(
                    "description",
                    StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        string heightCondition = heightElement?.Attributes()
            .FirstOrDefault(static attribute =>
                attribute.Name.LocalName.Equals(
                    "condition",
                    StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        string maxHeightCondition = Text(Descendant(maxHeight, "Condition"));
        string maximumHeightDescription = string.IsNullOrWhiteSpace(heightDescription)
            ? maxHeightCondition
            : heightDescription;
        double? maximumHeightMeters = heightElement is not null &&
            double.TryParse(
                heightElement.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double height) &&
            height > 0
                ? height
                : null;
        DateTimeOffset? maximumHeightObservedAt = maxHeight is null
            ? null
            : ReadDateTime(maxHeight, "DateTime");
        bool hasMaximumHeightInformation = maxHeight is not null &&
            (heightElement is not null ||
             !string.IsNullOrWhiteSpace(maxHeightCondition) ||
             maximumHeightObservedAt is not null);
        return new TsunamiArea(
            ReadTsunamiGrade(categoryName),
            condition.Contains("ただちに", StringComparison.Ordinal) ||
                condition.Contains("既に", StringComparison.Ordinal),
            areaName ?? Text(Descendant(source, "Name")),
            firstHeight is not null
                ? new TsunamiFirstHeight(ReadDateTime(firstHeight, "ArrivalTime"), condition)
                : null,
            hasMaximumHeightInformation
                ? new TsunamiMaximumHeight(
                    maximumHeightDescription,
                    maximumHeightMeters,
                    maximumHeightObservedAt,
                    heightCondition)
                : null);
    }

    private static string[] ReadTsunamiCategoryNames(XContainer document)
    {
        XElement? forecast = Descendant(document, "Forecast");
        return forecast is null
            ? []
            : Children(forecast, "Item")
                .Select(item => Text(Descendant(Descendant(item, "Category"), "Name")))
                .Where(static categoryName => !string.IsNullOrWhiteSpace(categoryName))
                .ToArray();
    }

    private static bool HasTsunamiWarningStateChange(XContainer document)
    {
        XElement? forecast = Descendant(document, "Forecast");
        if (forecast is null)
        {
            return false;
        }

        foreach (XElement item in Children(forecast, "Item"))
        {
            XElement? category = Child(item, "Category");
            if (category is null)
            {
                continue;
            }

            string currentName = Text(Descendant(Child(category, "Kind"), "Name"));
            string previousName = Text(Descendant(Child(category, "LastKind"), "Name"));
            int currentState = GetTsunamiWarningState(currentName);
            int previousState = GetTsunamiWarningState(previousName);
            if (currentState >= 0 && previousState >= 0 && currentState != previousState)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetTsunamiWarningState(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        if (IsTsunamiReleaseCategory(name))
        {
            return 0;
        }

        return ReadTsunamiGrade(name) switch
        {
            TsunamiGrade.Forecast => 1,
            TsunamiGrade.Watch => 2,
            TsunamiGrade.Warning => 3,
            TsunamiGrade.MajorWarning => 4,
            _ => -1,
        };
    }

    private static DateTimeOffset? ReadTsunamiObservationAsOf(
        XContainer reportHead,
        DateTimeOffset issuedAt,
        string telegramType)
    {
        if (telegramType is not ("VTSE51" or "VTSE52"))
        {
            return null;
        }

        Match match = TsunamiObservationAsOfPattern().Match(ReadHeadlineText(reportHead));
        if (!match.Success ||
            !TryParseJapaneseNumber(match.Groups["day"].Value, out int day) ||
            !TryParseJapaneseNumber(match.Groups["hour"].Value, out int hour) ||
            !TryParseJapaneseNumber(match.Groups["minute"].Value, out int minute))
        {
            return null;
        }

        DateTimeOffset localIssue = issuedAt.ToOffset(TimeSpan.FromHours(9));
        DateTimeOffset candidate;
        try
        {
            candidate = new DateTimeOffset(
                localIssue.Year,
                localIssue.Month,
                day,
                hour,
                minute,
                0,
                localIssue.Offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        // A telegram issued just after a month boundary can still refer to the
        // previous month's last observation.  Never interpret it as a future value.
        if (candidate > localIssue.AddHours(12))
        {
            DateTime previousMonth = new(localIssue.Year, localIssue.Month, 1);
            previousMonth = previousMonth.AddMonths(-1);
            int maximumDay = DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
            if (day > maximumDay)
            {
                return null;
            }

            candidate = new DateTimeOffset(
                previousMonth.Year,
                previousMonth.Month,
                day,
                hour,
                minute,
                0,
                localIssue.Offset);
        }

        return candidate;
    }

    private static bool TryParseJapaneseNumber(string value, out int number)
    {
        Span<char> normalized = stackalloc char[value.Length];
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            normalized[index] = character is >= '０' and <= '９'
                ? (char)('0' + character - '０')
                : character;
        }

        return int.TryParse(
            normalized,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static HypocenterInfo? ReadHypocenter(XElement earthquake)
    {
        XElement? hypocenter = Descendant(earthquake, "Hypocenter");
        if (hypocenter is null)
        {
            return null;
        }

        XElement? area = Descendant(hypocenter, "Area");
        string name = area is null ? string.Empty : Text(Descendant(area, "Name"));
        // AXIS reconstructs JMA element-basis values such as Coordinate and
        // Magnitude from JSON.  Those elements can also contain a child
        // <description> node, so XElement.Value would concatenate the value
        // and description (for example "4.9Ｍ４．９") and make numeric
        // parsing fail.  Only the element's direct text is the schema value.
        string coordinate = DirectText(Descendant(area, "Coordinate"));
        (double? latitude, double? longitude, int? depth) = ParseCoordinate(coordinate);
        XElement? magnitudeElement = Descendant(earthquake, "Magnitude");
        double? magnitude = double.TryParse(
            DirectText(magnitudeElement),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsedMagnitude) && parsedMagnitude > 0
                ? parsedMagnitude
                : null;
        string magnitudeDescription = magnitudeElement is null
            ? string.Empty
            : AttributeText(magnitudeElement, "description");
        if (string.IsNullOrWhiteSpace(magnitudeDescription) && magnitudeElement is not null)
        {
            // AXISのJSON再構成XMLではdescriptionが属性ではなく子要素になる場合もある。
            magnitudeDescription = Text(Child(magnitudeElement, "description"));
        }

        return new HypocenterInfo(
            name,
            name,
            latitude,
            longitude,
            depth,
            magnitude,
            Text(Descendant(hypocenter, "Accuracy")),
            magnitudeDescription);
    }

    private static (double? Latitude, double? Longitude, int? Depth) ParseCoordinate(string value)
    {
        Match match = CoordinatePattern().Match(value);
        if (!match.Success)
        {
            return (null, null, null);
        }

        double latitude = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        double longitude = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int depthMeters = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return (latitude, longitude, Math.Abs(depthMeters) / 1000);
    }

    private static JmaScale ReadScale(string value) => value.Trim() switch
    {
        "0" => JmaScale.Zero,
        "1" => JmaScale.One,
        "2" => JmaScale.Two,
        "3" => JmaScale.Three,
        "4" => JmaScale.Four,
        "5-" or "5弱" => JmaScale.FiveLower,
        "5+" or "5強" => JmaScale.FiveUpper,
        "6-" or "6弱" => JmaScale.SixLower,
        "6+" or "6強" => JmaScale.SixUpper,
        "7" => JmaScale.Seven,
        _ => JmaScale.Unknown,
    };

    private static DomesticTsunami ReadDomesticTsunami(string text)
    {
        if (text.Contains("津波の心配はありません", StringComparison.Ordinal))
        {
            return DomesticTsunami.None;
        }

        if (text.Contains("調査中", StringComparison.Ordinal))
        {
            return DomesticTsunami.Checking;
        }

        if (text.Contains("若干の海面変動", StringComparison.Ordinal))
        {
            return DomesticTsunami.NonEffective;
        }

        if (text.Contains("津波警報", StringComparison.Ordinal))
        {
            return DomesticTsunami.Warning;
        }

        return text.Contains("津波注意報", StringComparison.Ordinal)
            ? DomesticTsunami.Watch
            : DomesticTsunami.Unknown;
    }

    private static TsunamiGrade ReadTsunamiGrade(string value)
    {
        if (IsTsunamiReleaseCategory(value))
        {
            return TsunamiGrade.Unknown;
        }

        if (value.Contains("大津波警報", StringComparison.Ordinal))
        {
            return TsunamiGrade.MajorWarning;
        }

        if (value.Contains("津波警報", StringComparison.Ordinal))
        {
            return TsunamiGrade.Warning;
        }

        if (value.Contains("津波注意報", StringComparison.Ordinal))
        {
            return TsunamiGrade.Watch;
        }

        return value.Contains("津波予報", StringComparison.Ordinal)
            ? TsunamiGrade.Forecast
            : TsunamiGrade.Unknown;
    }

    private static bool IsTsunamiReleaseCategory(string value) =>
        value.Contains("解除", StringComparison.Ordinal);

    private static CorrectionType ReadCorrection(XContainer document)
    {
        string status = Text(Descendant(Descendant(document, "Control"), "Status"));
        string infoType = Text(Descendant(Descendant(document, "Head"), "InfoType"));
        return status.Contains("訂正", StringComparison.Ordinal) ||
            infoType.Contains("訂正", StringComparison.Ordinal)
                ? CorrectionType.Generic
                : CorrectionType.None;
    }

    private static bool IsTestTelegram(XContainer document)
    {
        string status = Text(Descendant(Descendant(document, "Control"), "Status"));
        return status.Contains("訓練", StringComparison.Ordinal) ||
            status.Contains("試験", StringComparison.Ordinal);
    }

    private static EventId ReadEventId(
        XContainer reportHead,
        string telegramType,
        DateTimeOffset issuedAt)
    {
        string eventId = Text(Descendant(reportHead, "EventID"));
        return EventId.Create(string.IsNullOrWhiteSpace(eventId)
            ? $"{telegramType}-{issuedAt:yyyyMMddHHmmssfff}"
            : eventId);
    }

    private static string DetectTelegramType(XContainer document)
    {
        // AXIS normally exposes the telegram type in the message UUID.  Some
        // converters preserve it as a root attribute, while others emit a
        // nested metadata element.  Accept both shapes before falling back to
        // the human-readable Control.Title.
        string metadataType = ReadTelegramTypeFromMetadata(document);
        if (!string.IsNullOrWhiteSpace(metadataType))
        {
            return metadataType;
        }

        string title = Text(Descendant(Descendant(document, "Control"), "Title"));
        if (title.Contains("記録的短時間大雨情報", StringComparison.Ordinal))
        {
            return "VPOA50";
        }

        if (title.Contains("気象防災速報", StringComparison.Ordinal))
        {
            string headTitle = Text(Descendant(Descendant(document, "Head"), "Title"));
            if (headTitle.Contains("竜巻", StringComparison.Ordinal))
            {
                return headTitle.Contains("目撃", StringComparison.Ordinal)
                    ? "VPHW51"
                    : "VPHW50";
            }

            if (title.Contains("潮位", StringComparison.Ordinal) ||
                headTitle.Contains("潮位", StringComparison.Ordinal))
            {
                return "VPBS51";
            }

            return "VPBS50";
        }

        if (title.Contains("竜巻注意情報", StringComparison.Ordinal))
        {
            return title.Contains("目撃情報付き", StringComparison.Ordinal)
                ? "VPHW51"
                : "VPHW50";
        }

        if (title.Contains("噴火警報・予報", StringComparison.Ordinal))
        {
            return "VFVO50";
        }

        if (title.Contains("噴火速報", StringComparison.Ordinal))
        {
            return "VFVO56";
        }

        if (title.Contains("気象警報・注意報", StringComparison.Ordinal) ||
            title.Contains("特別警報・警報・注意報", StringComparison.Ordinal))
        {
            if (title.Contains("集約通報", StringComparison.Ordinal))
            {
                return "VPWS50";
            }

            if (title.Contains("土砂", StringComparison.Ordinal)) return "VPWW56";
            if (title.Contains("高潮", StringComparison.Ordinal)) return "VPWW57";
            if (title.Contains("暴風", StringComparison.Ordinal)) return "VPWW58";
            if (title.Contains("波浪", StringComparison.Ordinal)) return "VPWW59";
            if (title.Contains("大雪", StringComparison.Ordinal)) return "VPWW60";
            if (title.Contains("その他", StringComparison.Ordinal)) return "VPWW61";
            if (title.Contains("大雨", StringComparison.Ordinal)) return "VPWW55";
            return title.Contains("Ｈ２７", StringComparison.Ordinal) ? "VPWW54" : "VPWW53";
        }
        if (title.StartsWith("津波警報・注意報・予報", StringComparison.Ordinal))
        {
            return "VTSE41";
        }

        if (title.StartsWith("津波情報", StringComparison.Ordinal))
        {
            return "VTSE51";
        }

        return title switch
        {
            "緊急地震速報（警報）" => "VXSE43",
            "緊急地震速報（地震動予報）" => "VXSE45",
            "震度速報" => "VXSE51",
            "震源に関する情報" => "VXSE52",
            "震源・震度に関する情報" => "VXSE53",
            "長周期地震動に関する観測情報" => "VXSE62",
            "南海トラフ地震臨時情報" => "VYSE50",
            "北海道・三陸沖後発地震注意情報" => "VYSE60",
            "沖合の津波観測に関する情報" => "VTSE52",
            _ => string.Empty,
        };
    }

    private static string ReadTelegramTypeFromMetadata(XContainer document)
    {
        IEnumerable<XElement> elements = document is XDocument xDocument &&
            xDocument.Root is not null
            ? xDocument.Root.DescendantsAndSelf()
            : document.Descendants();

        foreach (XElement element in elements)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (IsTelegramMetadataName(attribute.Name.LocalName) &&
                    TryExtractTelegramType(attribute.Value, out string telegramType))
                {
                    return telegramType;
                }
            }

            if (IsTelegramMetadataName(element.Name.LocalName) &&
                TryExtractTelegramType(element.Value, out string elementTelegramType))
            {
                return elementTelegramType;
            }
        }

        return string.Empty;
    }

    private static bool IsTelegramMetadataName(string localName) =>
        localName.Equals("uuid", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("telegramType", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("dataType", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractTelegramType(string value, out string telegramType)
    {
        Match match = TelegramTypePattern().Match(value);
        telegramType = match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        return match.Success;
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])V[A-Za-z0-9]{5}(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex TelegramTypePattern();

    [GeneratedRegex(
        @"(?<day>[0-9０-９]{1,2})日\s*(?<hour>[0-9０-９]{1,2})時\s*(?<minute>[0-9０-９]{1,2})分現在",
        RegexOptions.CultureInvariant)]
    private static partial Regex TsunamiObservationAsOfPattern();

    private static XDocument LoadSafe(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static XElement RequiredDescendant(XContainer container, string localName) =>
        Descendant(container, localName) ??
        throw new InvalidDataException($"Required XML element '{localName}' was missing.");

    private static XElement? Descendant(XContainer? container, string localName) =>
        container?.Descendants().FirstOrDefault(element => element.Name.LocalName == localName);

    private static IEnumerable<XElement> Descendants(XContainer container, string localName) =>
        container.Descendants().Where(element => element.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XContainer container, string localName) =>
        container.Elements().Where(element => element.Name.LocalName == localName);

    private static XElement? Child(XContainer container, string localName) =>
        container.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;

    private static string DirectText(XElement? element) => element is null
        ? string.Empty
        : string.Concat(element.Nodes().OfType<XText>().Select(static text => text.Value)).Trim();

    private static string AttributeText(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset? ReadDateTime(XContainer container, string localName) =>
        DateTimeOffset.TryParse(
            Text(Descendant(container, localName)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset value)
                ? value
                : null;

    private static NormalizeResult Invalid(string path, string message) =>
        NormalizeResult.Invalid(new ValidationIssue(path, message, ValidationSeverity.Error));

    [GeneratedRegex(@"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)([+-]\d+)(?:/.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CoordinatePattern();
}
