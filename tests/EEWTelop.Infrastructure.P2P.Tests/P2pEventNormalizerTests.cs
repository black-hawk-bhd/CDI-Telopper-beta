using EEWTelop.Application.Events;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class P2pEventNormalizerTests
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private readonly P2pEventNormalizer _normalizer = new(new EventSignatureBuilder());

    [TestMethod]
    public void Code551NormalizesToProviderIndependentQuakeEvent()
    {
        NormalizeResult result = NormalizeFixture("551-detail-scale.json");

        Assert.IsTrue(result.IsSuccess);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual("fixture-quake-551-001", quake.Id.Value);
        Assert.AreEqual(QuakeIssueType.DetailScale, quake.IssueType);
        Assert.AreEqual("6.0", Application.Formatting.MagnitudeFormatter.Format(
            quake.Earthquake.Hypocenter?.Magnitude));
        Assert.AreEqual("東京都府中市", quake.Points[0].DisplayName);
        Assert.HasCount(64, quake.Signature);
    }

    [TestMethod]
    public void Code551AcceptsLegacyUnderscoreIdAlias()
    {
        NormalizeResult result = NormalizeFixture("551-legacy-id.json");

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual("fixture-quake-legacy-001", quake.Id.Value);
        Assert.AreEqual(DomesticTsunami.Checking, quake.Earthquake.DomesticTsunami);
    }

    [TestMethod]
    [DataRow("ScaleOnly", "震度を訂正します")]
    [DataRow("DestinationOnly", "震源を訂正します")]
    [DataRow("ScaleAndDestination", "震度・震源を訂正します")]
    public void ProductionCorrectionFlowsFromProviderIntoCorrectionDisplay(
        string providerCorrection,
        string expectedText)
    {
        string json = ReadFixture("551-detail-scale.json")
            .Replace("\"correct\": \"None\"", $"\"correct\": \"{providerCorrection}\"",
                StringComparison.Ordinal);

        NormalizeResult result = _normalizer.Normalize(CreateRaw(json));

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(SourceMode.Production, quake.SourceMode);
        Assert.IsTrue(quake.IsCorrection);
        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        DisplayBlock correction = program.Pages
            .SelectMany(static page => page.Blocks)
            .Single(static block => block.StyleToken == DisplayStyleTokens.Correction);
        Assert.AreEqual("訂正", correction.Badge);
        Assert.AreEqual(expectedText, correction.PrimaryText);
    }

    [TestMethod]
    public void Code552NormalizesTsunamiAreasWithoutGuessingHeight()
    {
        NormalizeResult result = NormalizeFixture("552-tsunami.json");

        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.HasCount(2, tsunami.Areas);
        Assert.AreEqual(TsunamiGrade.MajorWarning, tsunami.Areas[0].Grade);
        Assert.IsNull(tsunami.Areas[0].MaximumHeight?.ValueMeters);
        Assert.AreEqual(1d, tsunami.Areas[1].MaximumHeight?.ValueMeters);
    }

    [TestMethod]
    public void Code556ReadsTopLevelIssueAndOnlyDeclaredAreas()
    {
        NormalizeResult result = NormalizeFixture("556-eew.json");

        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.AreEqual("fixture-eew-event-001", eew.Id.Value);
        Assert.AreEqual("3", eew.Issue.Serial);
        Assert.HasCount(2, eew.Areas);
        Assert.AreEqual(EewWarningKind.ForecastNotArrived, eew.Areas[0].WarningKind);
        Assert.AreEqual(EewWarningKind.Plum, eew.Areas[1].WarningKind);
        Assert.AreEqual(99, eew.Areas[1].ScaleTo);
        Assert.IsFalse(eew.IsFinal);
    }

    [TestMethod]
    public void MalformedAndMissingRequiredMessagesAreRejectedWithoutThrowing()
    {
        NormalizeResult malformed = NormalizeFixture("invalid-malformed.json");
        NormalizeResult missing = NormalizeFixture("invalid-missing-issue.json");

        Assert.AreEqual(NormalizeStatus.Invalid, malformed.Status);
        Assert.AreEqual(NormalizeStatus.Invalid, missing.Status);
        Assert.IsTrue(missing.Issues.Any(static issue => issue.Path == "issue"));
    }

    [TestMethod]
    public void UnknownCodeIsIgnoredWithDiagnosticIssue()
    {
        NormalizeResult result = NormalizeFixture("unknown-code.json");

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
        Assert.IsNull(result.Event);
        Assert.HasCount(1, result.Issues);
    }

    [TestMethod]
    public void UnknownIssueTypeIsAcceptedAsUnknownAndWarned()
    {
        string json = ReadFixture("551-detail-scale.json")
            .Replace("DetailScale", "FutureIssue", StringComparison.Ordinal);

        NormalizeResult result = _normalizer.Normalize(CreateRaw(json));

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.Unknown, quake.IssueType);
        Assert.IsTrue(result.Issues.Any(static issue => issue.Severity == ValidationSeverity.Warning));
    }

    [TestMethod]
    public void AllDocumentedProviderEnumsMapWithoutUnknown()
    {
        string[] issueTypes =
        ["ScalePrompt", "Destination", "ScaleAndDestination", "DetailScale", "Foreign", "Other"];
        string[] corrections =
        ["None", "Unknown", "ScaleOnly", "DestinationOnly", "ScaleAndDestination"];
        string[] domestic =
        ["None", "Unknown", "Checking", "NonEffective", "Watch", "Warning"];
        string[] foreign =
        [
            "None", "Unknown", "Checking", "NonEffectiveNearby", "WarningNearby",
            "WarningPacific", "WarningPacificWide", "WarningIndian", "WarningIndianWide", "Potential",
        ];
        string[] grades = ["MajorWarning", "Warning", "Watch", "Forecast", "Unknown"];

        Assert.IsFalse(issueTypes.Any(value =>
            P2pEnumMapper.ToQuakeIssueType(value) == QuakeIssueType.Unknown));
        Assert.IsTrue(corrections.All(value =>
            value == "Unknown" || P2pEnumMapper.ToCorrectionType(value) != CorrectionType.Unknown));
        Assert.IsTrue(domestic.All(value =>
            value == "Unknown" || P2pEnumMapper.ToDomesticTsunami(value) != DomesticTsunami.Unknown));
        Assert.IsTrue(foreign.All(value =>
            value == "Unknown" || P2pEnumMapper.ToForeignTsunami(value) != ForeignTsunami.Unknown));
        Assert.IsTrue(grades.All(value =>
            value == "Unknown" || P2pEnumMapper.ToTsunamiGrade(value) != TsunamiGrade.Unknown));
        Assert.AreEqual(EewWarningKind.ForecastNotArrived, P2pEnumMapper.ToEewWarningKind("10"));
        Assert.AreEqual(EewWarningKind.ForecastArrived, P2pEnumMapper.ToEewWarningKind("11"));
        Assert.AreEqual(EewWarningKind.Plum, P2pEnumMapper.ToEewWarningKind("19"));
        Assert.AreEqual(JmaScale.FiveLowerOrMore, P2pEnumMapper.ToScale(46));
    }

    [TestMethod]
    public void ProviderDtosAreNotPublicApi()
    {
        Type[] exportedDtoTypes = typeof(P2pEventNormalizer).Assembly
            .GetExportedTypes()
            .Where(static type => type.Namespace == "EEWTelop.Infrastructure.P2P.Dtos")
            .ToArray();

        Assert.IsEmpty(exportedDtoTypes, "Provider DTOs must stay behind the normalizer boundary.");
    }

    [TestMethod]
    public void TenThousandPointMessageNormalizesWithoutException()
    {
        object[] points = Enumerable.Range(0, 10_000)
            .Select(static index => (object)new
            {
                pref = $"固定県{index % 47}",
                addr = $"固定市{index}",
                isArea = false,
                scale = 30 + (index % 2 * 10),
            })
            .ToArray();
        string json = JsonSerializer.Serialize(new
        {
            id = "fixture-large-10000",
            code = 551,
            time = "2026/07/31 18:00:01.000",
            issue = new
            {
                source = "気象庁",
                time = "2026/07/31 18:00:00",
                type = "DetailScale",
                correct = "None",
            },
            earthquake = new
            {
                time = "2026/07/31 17:59:30",
                maxScale = 40,
                domesticTsunami = "None",
                foreignTsunami = "Unknown",
            },
            points,
            comments = new { freeFormComment = string.Empty },
        });

        NormalizeResult result = _normalizer.Normalize(CreateRaw(json));

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.HasCount(10_000, quake.Points);
    }

    [TestMethod]
    public void ExtremeNumericValuesBecomeUnknownInsteadOfOverflowing()
    {
        string json = ReadFixture("551-detail-scale.json")
            .Replace("\"maxScale\": 55", "\"maxScale\": 1e100", StringComparison.Ordinal)
            .Replace("\"depth\": 10", "\"depth\": 1e100", StringComparison.Ordinal);

        NormalizeResult result = _normalizer.Normalize(CreateRaw(json));

        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(JmaScale.Unknown, quake.Earthquake.MaximumScale);
        Assert.IsNull(quake.Earthquake.Hypocenter?.DepthKilometers);
    }

    [TestMethod]
    public void NormalizedFixtureFlowsIntoDisplayProgramWithoutProviderDto()
    {
        NormalizeResult normalized = NormalizeFixture("551-detail-scale.json");
        Assert.IsNotNull(normalized.Event);
        DisasterEvent disasterEvent = normalized.Event;
        var composer = new PageComposer();

        DisplayProgram program = composer.Compose(
            disasterEvent,
            AppSettings.CreateDefault().Display);

        Assert.AreEqual(disasterEvent.Id, program.EventId);
        StringAssert.Contains(program.Pages[0].AccessibleText, "マグニチュードは6.0と推定されます");
        Assert.IsTrue(program.Pages.SelectMany(static page => page.Blocks)
            .Any(static block => block.StyleToken == DisplayStyleTokens.Intensity));
    }

    private NormalizeResult NormalizeFixture(string fileName) =>
        _normalizer.Normalize(CreateRaw(ReadFixture(fileName)));

    private static RawProviderMessage CreateRaw(string json) =>
        new("P2PQuake", json, SourceMode.Production, ReceivedAt);

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
