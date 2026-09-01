using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Testing;

public static class TestScenarioCatalog
{
    private static readonly (string Prefecture, string Municipality)[] TrainingMunicipalities =
    [
        ("神奈川県", "横浜市"),
        ("神奈川県", "川崎市"),
        ("神奈川県", "相模原市"),
        ("神奈川県", "横須賀市"),
        ("神奈川県", "鎌倉市"),
        ("神奈川県", "小田原市"),
        ("東京都", "千代田区"),
        ("東京都", "新宿区"),
        ("東京都", "江東区"),
        ("東京都", "世田谷区"),
        ("東京都", "八王子市"),
        ("東京都", "町田市"),
        ("千葉県", "千葉市"),
        ("千葉県", "銚子市"),
        ("千葉県", "市川市"),
        ("千葉県", "船橋市"),
        ("千葉県", "館山市"),
        ("千葉県", "木更津市"),
        ("埼玉県", "さいたま市"),
        ("埼玉県", "川越市"),
        ("埼玉県", "熊谷市"),
        ("埼玉県", "川口市"),
        ("埼玉県", "所沢市"),
        ("埼玉県", "秩父市"),
        ("茨城県", "水戸市"),
        ("茨城県", "日立市"),
        ("茨城県", "土浦市"),
        ("茨城県", "古河市"),
        ("茨城県", "石岡市"),
        ("茨城県", "つくば市"),
        ("栃木県", "宇都宮市"),
        ("栃木県", "足利市"),
        ("栃木県", "栃木市"),
        ("栃木県", "佐野市"),
        ("栃木県", "鹿沼市"),
        ("栃木県", "日光市"),
        ("群馬県", "前橋市"),
        ("群馬県", "高崎市"),
        ("群馬県", "桐生市"),
        ("群馬県", "伊勢崎市"),
        ("群馬県", "太田市"),
        ("群馬県", "館林市"),
        ("山梨県", "甲府市"),
        ("山梨県", "富士吉田市"),
        ("山梨県", "都留市"),
        ("山梨県", "山梨市"),
        ("山梨県", "大月市"),
        ("山梨県", "笛吹市"),
        ("静岡県", "静岡市"),
        ("静岡県", "浜松市"),
        ("静岡県", "沼津市"),
        ("静岡県", "熱海市"),
        ("静岡県", "三島市"),
        ("静岡県", "富士市"),
        ("長野県", "長野市"),
        ("長野県", "松本市"),
        ("長野県", "上田市"),
        ("長野県", "岡谷市"),
        ("長野県", "飯田市"),
        ("長野県", "諏訪市"),
        ("新潟県", "新潟市"),
        ("新潟県", "長岡市"),
        ("新潟県", "三条市"),
        ("新潟県", "柏崎市"),
        ("新潟県", "新発田市"),
        ("新潟県", "上越市"),
        ("福島県", "福島市"),
        ("福島県", "会津若松市"),
        ("福島県", "郡山市"),
        ("福島県", "いわき市"),
        ("福島県", "白河市"),
        ("福島県", "須賀川市"),
        ("宮城県", "仙台市"),
        ("宮城県", "石巻市"),
        ("宮城県", "塩竈市"),
        ("宮城県", "気仙沼市"),
        ("宮城県", "白石市"),
        ("宮城県", "名取市"),
        ("岩手県", "盛岡市"),
        ("岩手県", "宮古市"),
        ("岩手県", "大船渡市"),
        ("岩手県", "花巻市"),
        ("岩手県", "北上市"),
        ("岩手県", "一関市"),
        ("青森県", "青森市"),
        ("青森県", "弘前市"),
        ("青森県", "八戸市"),
        ("青森県", "黒石市"),
        ("青森県", "五所川原市"),
        ("青森県", "むつ市"),
        ("北海道", "札幌市"),
        ("北海道", "函館市"),
        ("北海道", "小樽市"),
        ("北海道", "旭川市"),
        ("北海道", "室蘭市"),
        ("北海道", "釧路市"),
        ("北海道", "帯広市"),
        ("北海道", "北見市"),
        ("北海道", "岩見沢市"),
        ("北海道", "網走市"),
        ("北海道", "苫小牧市"),
        ("北海道", "根室市"),
        ("秋田県", "秋田市"),
        ("秋田県", "能代市"),
        ("秋田県", "横手市"),
        ("秋田県", "大館市"),
        ("秋田県", "男鹿市"),
        ("秋田県", "由利本荘市"),
        ("山形県", "山形市"),
        ("山形県", "米沢市"),
        ("山形県", "鶴岡市"),
        ("山形県", "酒田市"),
        ("山形県", "新庄市"),
        ("山形県", "寒河江市"),
        ("富山県", "富山市"),
        ("富山県", "高岡市"),
        ("富山県", "魚津市"),
        ("富山県", "氷見市"),
        ("富山県", "滑川市"),
        ("富山県", "黒部市"),
    ];

    private static readonly (string Prefecture, string Area, JmaScale Scale)[]
        TrainingSeismicAreas =
    [
        ("石川県", "石川県能登", JmaScale.Four),
        ("石川県", "石川県加賀", JmaScale.Three),
        ("新潟県", "新潟県佐渡", JmaScale.Three),
        ("富山県", "富山県東部", JmaScale.Three),
        ("長野県", "長野県北部", JmaScale.Three),
        ("岐阜県", "岐阜県飛騨", JmaScale.Three),
    ];

    private static readonly string[] TrainingTsunamiAreas =
    [
        "北海道太平洋沿岸東部",
        "北海道太平洋沿岸中部",
        "北海道太平洋沿岸西部",
        "青森県太平洋沿岸",
        "岩手県",
        "宮城県",
        "福島県",
        "茨城県",
        "千葉県九十九里・外房",
        "千葉県内房",
        "伊豆諸島",
        "相模湾・三浦半島",
        "静岡県",
    ];

    private static readonly string[] ExpandingEewPrefectures =
    [
        "千葉県",
        "東京都",
        "神奈川県",
        "埼玉県",
        "山梨県",
        "長野県",
        "静岡県",
        "岐阜県",
        "愛知県",
        "福島県",
        "宮城県",
        "岩手県",
        "青森県",
        "秋田県",
        "山形県",
    ];

    public static IReadOnlyList<TestScenario> Create(DateTimeOffset now)
    {
        DateTimeOffset issuedAt = now.ToOffset(TimeSpan.FromHours(9));
        return
        [
            new TestScenario(
                "eew-warning",
                "緊急地震速報（警報）",
                CreateEew(issuedAt, "warning", "警報", "2", isWarning: true, isFinal: false, isCancelled: false)),
            new TestScenario(
                "eew-cancel",
                "緊急地震速報（取消）",
                CreateEew(issuedAt, "cancel", "取消", "4", isWarning: true, isFinal: true, isCancelled: true)),
            CreateExpandingEewScenario(issuedAt),
            CreateConcurrentEewScenario(
                issuedAt,
                "eew-concurrent-two",
                "緊急地震速報（2件・5秒差）",
                cancellationCount: 0),
            CreateConcurrentEewScenario(
                issuedAt,
                "eew-concurrent-one-cancel",
                "緊急地震速報（1件取消＋1件発表中）",
                cancellationCount: 1),
            CreateConcurrentEewScenario(
                issuedAt,
                "eew-concurrent-two-cancel",
                "緊急地震速報（2件取消）",
                cancellationCount: 2),
            new TestScenario(
                "scale-prompt",
                "震度速報",
                CreateQuake(
                    "scale-prompt",
                    QuakeIssueType.ScalePrompt,
                    issuedAt,
                    CreateAreaPoints(),
                    domesticTsunami: DomesticTsunami.Checking)),
            new TestScenario(
                "detail-scale",
                "震源・震度情報",
                CreateQuake(
                    "detail-scale",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(12),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "tsunami-warning-quake",
                "津波情報発表中（地震情報）",
                CreateQuake(
                    "tsunami-warning-quake",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(6),
                    domesticTsunami: DomesticTsunami.Warning)),
            new TestScenario(
                "foreign",
                "遠地地震",
                CreateQuake(
                    "foreign",
                    QuakeIssueType.Foreign,
                    issuedAt,
                    [],
                    ForeignTsunami.WarningPacific,
                    hypocenterName: "チリ中部沿岸")),
            new TestScenario(
                "tsunami-major-warning",
                "大津波警報発表",
                CreateTsunami(
                    issuedAt,
                    isCancelled: false,
                    announcedGrade: TsunamiGrade.MajorWarning,
                    scenarioId: "tsunami-major-warning")),
            new TestScenario(
                "tsunami-warning",
                "津波警報発表",
                CreateTsunami(
                    issuedAt,
                    isCancelled: false,
                    announcedGrade: TsunamiGrade.Warning,
                    scenarioId: "tsunami-warning")),
            new TestScenario(
                "tsunami-watch",
                "津波注意報発表",
                CreateTsunami(
                    issuedAt,
                    isCancelled: false,
                    announcedGrade: TsunamiGrade.Watch,
                    scenarioId: "tsunami-watch")),
            new TestScenario(
                "tsunami-offshore-observation",
                "沖合の津波観測に関する情報（VTSE52）",
                CreateOffshoreTsunamiObservation(issuedAt)),
            new TestScenario("tsunami-13", "津波13地域（全種別）", CreateTsunami(issuedAt, isCancelled: false)),
            new TestScenario("cancel", "津波解除", CreateTsunami(issuedAt, isCancelled: true)),
            new TestScenario(
                "volcano-warning",
                "噴火警報・予報（VFVO50）",
                CreateVolcanoTraining(
                    issuedAt,
                    VolcanoInformationType.WarningForecast)),
            new TestScenario(
                "volcano-eruption-flash",
                "噴火速報（VFVO56）",
                CreateVolcanoTraining(
                    issuedAt,
                    VolcanoInformationType.EruptionFlash)),
            new TestScenario(
                "weather-special-warning",
                "気象特別警報",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-special-warning",
                    "VPWW58",
                    "暴風特別警報",
                    "35",
                    WeatherWarningLevel.SpecialWarning)),
            new TestScenario(
                "weather-warning",
                "気象警報",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-warning",
                    "VPWW59",
                    "波浪警報",
                    "10",
                    WeatherWarningLevel.Warning)),
            new TestScenario(
                "weather-advisory",
                "気象注意報",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-advisory",
                    "VPWW61",
                    "雷注意報",
                    "14",
                    WeatherWarningLevel.Advisory)),
            new TestScenario(
                "weather-level5",
                "気象注警報 レベル5（特別警報相当）",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-level5",
                    "VPWW55",
                    "レベル５大雨特別警報",
                    "L5",
                    WeatherWarningLevel.SpecialWarning)),
            new TestScenario(
                "weather-level4",
                "気象注警報 レベル4（警報相当）",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-level4",
                    "VPWW56",
                    "レベル４土砂災害危険警報",
                    "L4",
                    WeatherWarningLevel.Warning)),
            new TestScenario(
                "weather-level3",
                "気象注警報 レベル3（警報相当）",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-level3",
                    "VPWW57",
                    "レベル３高潮警報",
                    "L3",
                    WeatherWarningLevel.Warning)),
            new TestScenario(
                "weather-level2",
                "気象注警報 レベル2（注意報相当）",
                CreateWeatherTraining(
                    issuedAt,
                    "weather-level2",
                    "VPWW55",
                    "レベル２大雨注意報",
                    "L2",
                    WeatherWarningLevel.Advisory)),
            new TestScenario(
                "weather-warning-cancel",
                "気象警報・注意報解除",
                CreateWeatherWarningCancellation(issuedAt)),
            new TestScenario(
                "large",
                "大量地点（最大震度7）",
                CreateQuake(
                    "large",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.Seven),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "large-6-upper",
                "大量地点（最大震度6強）",
                CreateQuake(
                    "large-6-upper",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.SixUpper),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "large-6-lower",
                "大量地点（最大震度6弱）",
                CreateQuake(
                    "large-6-lower",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.SixLower),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "large-5-upper",
                "大量地点（最大震度5強）",
                CreateQuake(
                    "large-5-upper",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.FiveUpper),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "large-5-lower",
                "大量地点（最大震度5弱）",
                CreateQuake(
                    "large-5-lower",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.FiveLower),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "large-4",
                "大量地点（最大震度4）",
                CreateQuake(
                    "large-4",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.Four),
                    domesticTsunami: DomesticTsunami.None)),
            new TestScenario(
                "large-3",
                "大量地点（最大震度3）",
                CreateQuake(
                    "large-3",
                    QuakeIssueType.DetailScale,
                    issuedAt,
                    CreatePoints(120, JmaScale.Three),
                    domesticTsunami: DomesticTsunami.None)),
        ];
    }

    private static EewEvent CreateEew(
        DateTimeOffset issuedAt,
        string id,
        string issueType,
        string serial,
        bool isWarning,
        bool isFinal,
        bool isCancelled,
        string hypocenterName = "房総半島南方沖",
        IReadOnlyList<string>? prefectures = null,
        DateTimeOffset? originTime = null)
    {
        var issue = new IssueInfo("気象庁", issuedAt, issueType, CorrectionType.None, serial);
        DateTimeOffset effectiveOriginTime = originTime ?? issuedAt.AddSeconds(-20);
        var earthquake = new EarthquakeInfo(
            effectiveOriginTime,
            effectiveOriginTime.AddSeconds(25),
            new HypocenterInfo(hypocenterName, hypocenterName, 34.5, 140.2, 20, 6.4, ""),
            JmaScale.SixLower,
            DomesticTsunami.Checking,
            ForeignTsunami.Unknown);
        IReadOnlyList<string> effectivePrefectures = prefectures ?? ["千葉県", "東京都", "神奈川県"];
        EewArea[] areas = effectivePrefectures
            .Select((prefecture, index) => new EewArea(
                prefecture,
                $"{prefecture}対象地域",
                index == 0 ? JmaScale.SixLower : JmaScale.FiveUpper,
                index == 0 ? 60 : 55,
                EewWarningKind.ForecastNotArrived,
                issuedAt.AddSeconds(7 + index * 3)))
            .ToArray();
        return new EewEvent(
            EventId.Create($"test-eew-{id}"),
            "P2PQuake",
            issuedAt,
            issuedAt,
            $"TEST-EEW-{id.ToUpperInvariant()}-{issueType}-{serial}",
            SourceMode.ManualTest,
            issue,
            earthquake,
            areas,
            isWarning,
            isFinal,
            isCancelled,
            isTest: true);
    }

    private static TestScenario CreateExpandingEewScenario(DateTimeOffset issuedAt)
    {
        DateTimeOffset originTime = issuedAt.AddSeconds(-20);
        TestScenarioStep[] steps = Enumerable.Range(1, ExpandingEewPrefectures.Length)
            .Select(reportNumber =>
            {
                DateTimeOffset reportIssuedAt = issuedAt.AddSeconds((reportNumber - 1) * 2);
                EewEvent report = CreateEew(
                    reportIssuedAt,
                    "expanding-areas",
                    "警報",
                    reportNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    isWarning: true,
                    isFinal: false,
                    isCancelled: false,
                    prefectures: ExpandingEewPrefectures.Take(reportNumber).ToArray(),
                    originTime: originTime);
                return new TestScenarioStep(
                    reportNumber == 1 ? TimeSpan.Zero : TimeSpan.FromSeconds(2),
                    report);
            })
            .ToArray();

        return new TestScenario(
            "eew-expanding-15",
            "緊急地震速報（第1～15報・地域拡大）",
            steps[0].Event,
            steps);
    }

    private static TestScenario CreateConcurrentEewScenario(
        DateTimeOffset issuedAt,
        string scenarioId,
        string label,
        int cancellationCount)
    {
        EewEvent first = CreateEew(
            issuedAt,
            $"{scenarioId}-a",
            "警報",
            "1",
            isWarning: true,
            isFinal: false,
            isCancelled: false,
            hypocenterName: "山梨県東部・富士五湖",
            prefectures: ["山梨県", "東京都", "神奈川県", "静岡県"]);
        EewEvent second = CreateEew(
            issuedAt.AddSeconds(5),
            $"{scenarioId}-b",
            "警報",
            "1",
            isWarning: true,
            isFinal: false,
            isCancelled: false,
            hypocenterName: "東京湾",
            prefectures: ["千葉県", "東京都", "神奈川県", "埼玉県"]);
        var steps = new List<TestScenarioStep>
        {
            new(TimeSpan.Zero, first),
            new(TimeSpan.FromSeconds(5), second),
        };

        if (cancellationCount >= 1)
        {
            steps.Add(new TestScenarioStep(
                TimeSpan.FromSeconds(5),
                CreateEew(
                    issuedAt.AddSeconds(10),
                    $"{scenarioId}-a",
                    "取消",
                    "2",
                    isWarning: true,
                    isFinal: true,
                    isCancelled: true,
                    hypocenterName: "山梨県東部・富士五湖")));
        }

        if (cancellationCount >= 2)
        {
            steps.Add(new TestScenarioStep(
                TimeSpan.FromSeconds(5),
                CreateEew(
                    issuedAt.AddSeconds(15),
                    $"{scenarioId}-b",
                    "取消",
                    "2",
                    isWarning: true,
                    isFinal: true,
                    isCancelled: true,
                    hypocenterName: "東京湾")));
        }

        return new TestScenario(scenarioId, label, first, steps);
    }

    private static QuakeEvent CreateQuake(
        string id,
        QuakeIssueType issueType,
        DateTimeOffset issuedAt,
        QuakePoint[] points,
        ForeignTsunami foreignTsunami = ForeignTsunami.None,
        CorrectionType correction = CorrectionType.None,
        DomesticTsunami domesticTsunami = DomesticTsunami.Unknown,
        string hypocenterName = "相模湾")
    {
        var issue = new IssueInfo("気象庁", issuedAt, issueType.ToString(), correction);
        var earthquake = new EarthquakeInfo(
            issuedAt.AddMinutes(-2),
            null,
            new HypocenterInfo(hypocenterName, hypocenterName, 35.1, 139.4, 30, 5.8, ""),
            points.Length == 0 ? JmaScale.Four : points.Max(static point => point.Scale),
            domesticTsunami,
            foreignTsunami);
        return new QuakeEvent(
            EventId.Create($"test-{id}"),
            "P2PQuake",
            issuedAt,
            issuedAt,
            $"TEST-{id}",
            SourceMode.ManualTest,
            issue,
            issueType,
            earthquake,
            points,
            issueType == QuakeIssueType.Foreign
                ? "太平洋で津波が発生する可能性があります。"
                : "");
    }

    private static QuakePoint[] CreatePoints(
        int count,
        JmaScale maximumScale = JmaScale.SixLower)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, TrainingMunicipalities.Length);

        JmaScale[] allScales =
        [
            JmaScale.Seven,
            JmaScale.SixUpper,
            JmaScale.SixLower,
            JmaScale.FiveUpper,
            JmaScale.FiveLower,
            JmaScale.Four,
            JmaScale.Three,
        ];
        int maximumIndex = Array.IndexOf(allScales, maximumScale);
        if (maximumIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumScale),
                maximumScale,
                "Maximum training scale must be between intensity 3 and 7.");
        }

        JmaScale[] scales = allScales.Skip(maximumIndex).ToArray();
        return TrainingMunicipalities
            .Take(count)
            .Select((place, index) => new QuakePoint(
                place.Prefecture,
                place.Municipality,
                IsArea: false,
                scales[Math.Min(scales.Length - 1, index / 4)],
                place.Prefecture + place.Municipality))
            .ToArray();
    }

    private static QuakePoint[] CreateAreaPoints() => TrainingSeismicAreas
        .Select(static area => new QuakePoint(
            area.Prefecture,
            area.Area,
            IsArea: true,
            area.Scale,
            area.Area))
        .ToArray();

    private static TsunamiEvent CreateTsunami(
        DateTimeOffset issuedAt,
        bool isCancelled,
        TsunamiGrade? announcedGrade = null,
        string scenarioId = "tsunami")
    {
        var issue = new IssueInfo("気象庁", issuedAt, "津波予報", CorrectionType.None);
        TsunamiArea[] areas = TrainingTsunamiAreas
            .Select((areaName, index) =>
            {
                TsunamiGrade grade = announcedGrade ?? (index < 2
                    ? TsunamiGrade.MajorWarning
                    : index < 5
                        ? TsunamiGrade.Warning
                        : index < 10
                        ? TsunamiGrade.Watch
                        : TsunamiGrade.Forecast);
                (string description, double meters) = grade switch
                {
                    TsunamiGrade.MajorWarning => ("５ｍ", 5),
                    TsunamiGrade.Warning => ("３ｍ", 3),
                    _ => ("１ｍ", 1),
                };
                return new TsunamiArea(
                    grade,
                    Immediate: index % 4 == 0,
                    areaName,
                    new TsunamiFirstHeight(
                        issuedAt.AddMinutes(index + 1),
                        index % 4 == 0 ? "ただちに来襲と予測" : ""),
                    new TsunamiMaximumHeight(description, meters));
            })
            .ToArray();
        return new TsunamiEvent(
            EventId.Create(isCancelled ? "test-tsunami-cancel" : $"test-{scenarioId}"),
            "P2PQuake",
            issuedAt,
            issuedAt,
            isCancelled ? "TEST-TSUNAMI-CANCEL" : $"TEST-{scenarioId.ToUpperInvariant()}",
            SourceMode.ManualTest,
            issue,
            areas,
            isCancelled,
            issuedAt.AddHours(6));
    }

    private static TsunamiEvent CreateOffshoreTsunamiObservation(DateTimeOffset issuedAt)
    {
        (string Name, string Description, double HeightMeters)[] stations =
        [
            ("静岡御前崎沖", "１．８ｍ", 1.8),
            ("三重尾鷲沖", "２．０ｍ", 2.0),
            ("三重南東沖８０ｋｍＡ", "１．１ｍ", 1.1),
            ("和歌山沖７０ｋｍＢ", "０．８ｍ", 0.8),
            ("和歌山白浜沖", "１．２ｍ", 1.2),
            ("徳島海陽沖", "１．１ｍ", 1.1),
            ("高知沖１００ｋｍＡ", "０．８ｍ", 0.8),
            ("高知足摺岬沖", "１．７ｍ", 1.7),
        ];
        TsunamiArea[] areas = stations
            .Select(station => new TsunamiArea(
                TsunamiGrade.Unknown,
                Immediate: false,
                station.Name,
                new TsunamiFirstHeight(issuedAt.AddMinutes(-5), "押し"),
                new TsunamiMaximumHeight(
                    station.Description,
                    station.HeightMeters,
                    issuedAt.AddMinutes(-2)))
            {
                Role = TsunamiInformationRole.OffshoreObservation,
            })
            .ToArray();
        var issue = new IssueInfo(
            "気象庁",
            issuedAt,
            "VTSE52",
            CorrectionType.None);
        return new TsunamiEvent(
            EventId.Create("test-tsunami-offshore-observation"),
            "nii-jma-xml",
            issuedAt,
            issuedAt,
            "TEST-TSUNAMI-OFFSHORE-OBSERVATION",
            SourceMode.ManualTest,
            issue,
            areas,
            isCancelled: false,
            issuedAt.AddHours(6));
    }

    private static VolcanoEvent CreateVolcanoTraining(
        DateTimeOffset issuedAt,
        VolcanoInformationType informationType)
    {
        bool eruptionFlash = informationType == VolcanoInformationType.EruptionFlash;
        string telegramType = eruptionFlash ? "VFVO56" : "VFVO50";
        string scenarioId = eruptionFlash ? "volcano-eruption-flash" : "volcano-warning";
        var issue = new IssueInfo(
            "気象庁",
            issuedAt,
            telegramType,
            CorrectionType.None);
        return new VolcanoEvent(
            EventId.Create($"test-{scenarioId}"),
            "dmdata.jp",
            issuedAt,
            issuedAt,
            $"TEST-{scenarioId.ToUpperInvariant()}",
            SourceMode.ManualTest,
            issue,
            informationType,
            "桜島",
            "506",
            eruptionFlash ? VolcanoAlertLevel.Unknown : VolcanoAlertLevel.Level3,
            eruptionFlash ? string.Empty : "レベル３（入山規制）",
            eruptionFlash
                ? "桜島で噴火が発生"
                : "桜島に火口周辺警報を発表し、噴火警戒レベルを３に引き上げました。",
            eruptionFlash
                ? "桜島で噴火が発生しました。"
                : "南岳山頂火口では、活発な噴火活動が続いています。",
            eruptionFlash
                ? string.Empty
                : "火口からおおむね２kmの範囲では、大きな噴石及び火砕流に警戒してください。",
            [
                new VolcanoTargetArea(
                    "鹿児島市",
                    "4620100",
                    eruptionFlash ? "噴火" : "火口周辺警報",
                    eruptionFlash ? "52" : "12",
                    "発表"),
            ],
            eruptionFlash ? issuedAt.AddMinutes(-1) : null,
            isCancelled: false,
            isWarning: !eruptionFlash,
            alertLevelCode: eruptionFlash ? string.Empty : "13",
            alertCondition: eruptionFlash ? string.Empty : "引上げ",
            eventTimeIsApproximate: eruptionFlash,
            eventTimePrecision: eruptionFlash ? "yyyy-mm-ddThh:mm" : string.Empty);
    }

    private static WeatherWarningEvent CreateWeatherWarningCancellation(
        DateTimeOffset issuedAt)
    {
        WeatherWarningItem[] items =
        [
            new(
                "熊本市",
                "4310000",
                "大雨警報",
                "03",
                WeatherWarningLevel.Warning,
                "解除",
                IsActive: false),
            new(
                "八代市",
                "4320200",
                "洪水警報",
                "04",
                WeatherWarningLevel.Warning,
                "解除",
                IsActive: false),
            new(
                "天草市",
                "4321500",
                "雷注意報",
                "14",
                WeatherWarningLevel.Advisory,
                "解除",
                IsActive: false),
        ];
        var issue = new IssueInfo(
            "熊本地方気象台",
            issuedAt,
            "VPWW54",
            CorrectionType.None);
        return new WeatherWarningEvent(
            EventId.Create("test-weather-warning-cancel"),
            "dmdata.jp",
            issuedAt,
            issuedAt,
            "TEST-WEATHER-CANCEL",
            SourceMode.ManualTest,
            issue,
            "気象警報・注意報を解除します。",
            items,
            isCancelled: true);
    }

    private static WeatherWarningEvent CreateWeatherTraining(
        DateTimeOffset issuedAt,
        string scenarioId,
        string telegramType,
        string kindName,
        string kindCode,
        WeatherWarningLevel level)
    {
        var issue = new IssueInfo(
            "熊本地方気象台",
            issuedAt,
            telegramType,
            CorrectionType.None);
        return new WeatherWarningEvent(
            EventId.Create($"test-{scenarioId}"),
            "dmdata.jp",
            issuedAt,
            issuedAt,
            $"TEST-{scenarioId.ToUpperInvariant()}",
            SourceMode.ManualTest,
            issue,
            $"{kindName}を発表しました。",
            [
                new WeatherWarningItem(
                    "熊本市",
                    "4310000",
                    kindName,
                    kindCode,
                    level,
                    "発表",
                    IsActive: true),
            ],
            isCancelled: false);
    }
}

public sealed record TestScenario(
    string Id,
    string Label,
    DisasterEvent Event,
    IReadOnlyList<TestScenarioStep>? Timeline = null)
{
    public IReadOnlyList<TestScenarioStep> Steps =>
        Timeline ?? [new TestScenarioStep(TimeSpan.Zero, Event)];
}

public sealed record TestScenarioStep(
    TimeSpan DelayAfterPrevious,
    DisasterEvent Event);
