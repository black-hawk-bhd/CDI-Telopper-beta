namespace EEWTelop.Domain.Events;

public abstract record DisasterEvent
{
    protected DisasterEvent(
        EventId id,
        string provider,
        int providerCode,
        EventKind kind,
        DateTimeOffset issuedAt,
        DateTimeOffset receivedAt,
        string signature,
        SourceMode sourceMode,
        bool isCorrection,
        bool isCancelled)
    {
        Id = id;
        Provider = provider;
        ProviderCode = providerCode;
        Kind = kind;
        IssuedAt = issuedAt;
        ReceivedAt = receivedAt;
        Signature = signature;
        SourceMode = sourceMode;
        IsCorrection = isCorrection;
        IsCancelled = isCancelled;
    }

    public EventId Id { get; }

    public string Provider { get; }

    public int ProviderCode { get; }

    public EventKind Kind { get; }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset ReceivedAt { get; }

    public string Signature { get; init; }

    public SourceMode SourceMode { get; }

    public bool IsCorrection { get; }

    public bool IsCancelled { get; }
}

public sealed record QuakeEvent : DisasterEvent
{
    public QuakeEvent(
        EventId id,
        string provider,
        DateTimeOffset issuedAt,
        DateTimeOffset receivedAt,
        string signature,
        SourceMode sourceMode,
        IssueInfo issue,
        QuakeIssueType issueType,
        EarthquakeInfo earthquake,
        IReadOnlyList<QuakePoint> points,
        string freeFormComment,
        LongPeriodIntensityInfo? longPeriodIntensity = null,
        bool isCancelled = false,
        string headline = "")
        : base(
            id,
            provider,
            551,
            EventKind.Quake,
            issuedAt,
            receivedAt,
            signature,
            sourceMode,
            issue.Correction is not (CorrectionType.None or CorrectionType.Unknown),
            isCancelled)
    {
        Issue = issue;
        IssueType = issueType;
        Earthquake = earthquake;
        Points = points;
        FreeFormComment = freeFormComment;
        LongPeriodIntensity = longPeriodIntensity;
        Headline = headline;
    }

    public IssueInfo Issue { get; }

    public QuakeIssueType IssueType { get; }

    public EarthquakeInfo Earthquake { get; }

    public IReadOnlyList<QuakePoint> Points { get; }

    public string FreeFormComment { get; }

    public LongPeriodIntensityInfo? LongPeriodIntensity { get; }

    public string Headline { get; }
}

public sealed record TsunamiEvent : DisasterEvent
{
    public TsunamiEvent(
        EventId id,
        string provider,
        DateTimeOffset issuedAt,
        DateTimeOffset receivedAt,
        string signature,
        SourceMode sourceMode,
        IssueInfo issue,
        IReadOnlyList<TsunamiArea> areas,
        bool isCancelled,
        DateTimeOffset? expireAt,
        DateTimeOffset? observationAsOf = null)
        : base(
            id,
            provider,
            552,
            EventKind.Tsunami,
            issuedAt,
            receivedAt,
            signature,
            sourceMode,
            isCorrection: false,
            isCancelled)
    {
        Issue = issue;
        Areas = areas;
        ExpireAt = expireAt;
        ObservationAsOf = observationAsOf;
    }

    public IssueInfo Issue { get; }

    public IReadOnlyList<TsunamiArea> Areas { get; }

    public DateTimeOffset? ExpireAt { get; }

    /// <summary>
    /// The observation reference time stated in a VTSE51/VTSE52 headline.
    /// This is intentionally separate from the telegram issue time.
    /// </summary>
    public DateTimeOffset? ObservationAsOf { get; }

    /// <summary>
    /// True when a VTSE41 forecast item explicitly changes from its LastKind
    /// warning grade to a different current Kind grade.
    /// </summary>
    public bool WarningStateChanged { get; init; }
}

public sealed record EewEvent : DisasterEvent
{
    public EewEvent(
        EventId id,
        string provider,
        DateTimeOffset issuedAt,
        DateTimeOffset receivedAt,
        string signature,
        SourceMode sourceMode,
        IssueInfo issue,
        EarthquakeInfo? earthquake,
        IReadOnlyList<EewArea> areas,
        bool isWarning,
        bool isFinal,
        bool isCancelled,
        bool isTest)
        : base(
            id,
            provider,
            556,
            EventKind.Eew,
            issuedAt,
            receivedAt,
            signature,
            sourceMode,
            isCorrection: false,
            isCancelled)
    {
        Issue = issue;
        Earthquake = earthquake;
        Areas = areas;
        IsWarning = isWarning;
        IsFinal = isFinal;
        IsTest = isTest;
    }

    public IssueInfo Issue { get; }

    public EarthquakeInfo? Earthquake { get; }

    public IReadOnlyList<EewArea> Areas { get; }

    public bool IsWarning { get; }

    public bool IsFinal { get; }

    public bool IsTest { get; }
}

public sealed record WeatherWarningEvent : DisasterEvent
{
    public const int InternalProviderCode = 600;

    public WeatherWarningEvent(
        EventId id,
        string provider,
        DateTimeOffset issuedAt,
        DateTimeOffset receivedAt,
        string signature,
        SourceMode sourceMode,
        IssueInfo issue,
        string headline,
        IReadOnlyList<WeatherWarningItem> items,
        bool isCancelled,
        WeatherInformationType informationType = WeatherInformationType.WarningAndAdvisory,
        DateTimeOffset? validUntil = null)
        : base(
            id,
            provider,
            InternalProviderCode,
            EventKind.WeatherWarning,
            issuedAt,
            receivedAt,
            signature,
            sourceMode,
            issue.Correction is not (CorrectionType.None or CorrectionType.Unknown),
            isCancelled)
    {
        Issue = issue;
        Headline = headline;
        Items = items;
        InformationType = informationType;
        ValidUntil = validUntil;
    }

    public IssueInfo Issue { get; }

    public string Headline { get; }

    public IReadOnlyList<WeatherWarningItem> Items { get; }

    public WeatherInformationType InformationType { get; }

    public DateTimeOffset? ValidUntil { get; }

    public WeatherWarningLevel MaximumLevel => Items
        .Where(static item => item.IsActive)
        .Select(static item => item.Level)
        .DefaultIfEmpty(WeatherWarningLevel.Unknown)
        .Max();

    public WeatherWarningEvent WithItems(IReadOnlyList<WeatherWarningItem> items) => new(
        Id,
        Provider,
        IssuedAt,
        ReceivedAt,
        Signature,
        SourceMode,
        Issue,
        Headline,
        items,
        IsCancelled,
        InformationType,
        ValidUntil);
}

public sealed record VolcanoEvent : DisasterEvent
{
    public const int InternalProviderCode = 700;

    public VolcanoEvent(
        EventId id,
        string provider,
        DateTimeOffset issuedAt,
        DateTimeOffset receivedAt,
        string signature,
        SourceMode sourceMode,
        IssueInfo issue,
        VolcanoInformationType informationType,
        string volcanoName,
        string volcanoCode,
        VolcanoAlertLevel alertLevel,
        string alertLevelText,
        string headline,
        string activity,
        string prevention,
        IReadOnlyList<VolcanoTargetArea> targetAreas,
        DateTimeOffset? eventTime,
        bool isCancelled,
        bool isWarning = false,
        string alertLevelCode = "",
        string alertCondition = "",
        string previousAlertLevelText = "",
        string previousAlertLevelCode = "",
        bool eventTimeIsApproximate = false,
        string eventTimePrecision = "",
        bool isTelegramCancellation = false,
        string notice = "",
        string otherInfo = "",
        string appendix = "",
        string contentText = "",
        string bodyText = "")
        : base(
            id,
            provider,
            InternalProviderCode,
            EventKind.Volcano,
            issuedAt,
            receivedAt,
            signature,
            sourceMode,
            issue.Correction is not (CorrectionType.None or CorrectionType.Unknown),
            isCancelled)
    {
        Issue = issue;
        InformationType = informationType;
        VolcanoName = volcanoName;
        VolcanoCode = volcanoCode;
        AlertLevel = alertLevel;
        AlertLevelText = alertLevelText;
        Headline = headline;
        Activity = activity;
        Prevention = prevention;
        TargetAreas = targetAreas;
        EventTime = eventTime;
        IsWarning = isWarning;
        AlertLevelCode = alertLevelCode;
        AlertCondition = alertCondition;
        PreviousAlertLevelText = previousAlertLevelText;
        PreviousAlertLevelCode = previousAlertLevelCode;
        EventTimeIsApproximate = eventTimeIsApproximate;
        EventTimePrecision = eventTimePrecision;
        IsTelegramCancellation = isTelegramCancellation;
        Notice = notice;
        OtherInfo = otherInfo;
        Appendix = appendix;
        ContentText = contentText;
        BodyText = bodyText;
    }

    public IssueInfo Issue { get; }
    public VolcanoInformationType InformationType { get; }
    public string VolcanoName { get; }
    public string VolcanoCode { get; }
    public VolcanoAlertLevel AlertLevel { get; }
    public string AlertLevelText { get; }
    public string Headline { get; }
    public string Activity { get; }
    public string Prevention { get; }
    public IReadOnlyList<VolcanoTargetArea> TargetAreas { get; }
    public DateTimeOffset? EventTime { get; }
    public bool IsWarning { get; }
    public string AlertLevelCode { get; }
    public string AlertCondition { get; }
    public string PreviousAlertLevelText { get; }
    public string PreviousAlertLevelCode { get; }
    public bool EventTimeIsApproximate { get; }
    public string EventTimePrecision { get; }
    public bool IsTelegramCancellation { get; }
    public string Notice { get; }
    public string OtherInfo { get; }
    public string Appendix { get; }
    public string ContentText { get; }
    public string BodyText { get; }
}
