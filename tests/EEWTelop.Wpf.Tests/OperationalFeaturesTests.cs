using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Operations;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Infrastructure.Operations;
using EEWTelop.Infrastructure.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class OperationalFeaturesTests
{
    private static readonly int[] OverseasTsunamiSampleRowsPerPage = [1, 2, 2, 2, 2];
    private static readonly string[] OverseasTsunamiSampleObservations =
    [
        "エクアドル・ガラパゴス諸島サンタクルス島|0.7 m",
        "チリ・タルカワノ|2.3 m",
        "チリ・バルパライソ|1.3 m",
        "チリ・コキンボ|1.3 m",
        "チリ・コラル|0.9 m",
        "チリ・アンクド|0.6 m",
        "チリ・アントファガスタ|0.5 m",
        "チリ・イースター島|0.4 m",
        "仏領マルキーズ諸島・ヒバオア島|1.8 m",
    ];
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"qt-operational-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void AlertsAreCoalescedAndRecoveryIsPublishedOnce()
    {
        var center = new OperationalAlertCenter(TimeSpan.FromSeconds(60));
        var published = new List<OperationalAlert>();
        center.AlertRaised += published.Add;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        center.Raise(new OperationalAlert("disk", OperationalAlertSeverity.Warning, "容量", "少ない", now));
        center.Raise(new OperationalAlert("disk", OperationalAlertSeverity.Warning, "容量", "少ない", now.AddSeconds(5)));
        center.Recover("disk", "復旧", "回復", now.AddSeconds(10));
        center.Recover("disk", "復旧", "回復", now.AddSeconds(11));

        Assert.AreEqual(2, published.Count);
        Assert.IsTrue(published[1].IsRecovery);
    }

    [TestMethod]
    public void OperationalAlertExposesLocalDisplayTimeWithoutChangingUtcValue()
    {
        DateTimeOffset utc = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var alert = new OperationalAlert(
            "test", OperationalAlertSeverity.Warning, "確認", "本文", utc);

        Assert.AreEqual(utc, alert.RaisedAtUtc);
        Assert.AreEqual(utc.ToLocalTime(), alert.RaisedAtLocal);
    }

    [TestMethod]
    public void OperationalDiagnosticRunBindingsDoNotWriteBackToReadOnlyModels()
    {
        string xamlPath = FindRepositoryFile("src", "EEWTelop.Wpf", "ControlWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        StringAssert.Contains(xaml, "RaisedAtLocal, Mode=OneWay");
        StringAssert.Contains(xaml, "Title, Mode=OneWay");
        StringAssert.Contains(xaml, "Message, Mode=OneWay");
        StringAssert.Contains(xaml, "Status, Mode=OneWay");
        StringAssert.Contains(xaml, "ProviderA, Mode=OneWay");
        StringAssert.Contains(xaml, "ProviderB, Mode=OneWay");
        StringAssert.Contains(xaml, "Summary, Mode=OneWay");
    }

    [TestMethod]
    public async Task ProfileNeverExportsSecretsAndRestoresCurrentDeviceSecrets()
    {
        var store = new JsonSettingsProfileStore(Path.Combine(_directory, "profiles"));
        AppSettings saved = AppSettings.CreateDefault() with
        {
            Provider = AppSettings.CreateDefault().Provider with
            {
                DmdataProtectedCredential = "saved-dmdata-secret",
                AxisProtectedAccessToken = "saved-axis-secret",
            },
            Obs = AppSettings.CreateDefault().Obs with { WebSocketProtectedPassword = "saved-obs-secret" },
        };
        await store.SaveAsync("production", saved, "test");
        string export = Path.Combine(_directory, "production.qtprofile.json");
        await store.ExportAsync("production", export);
        string text = await File.ReadAllTextAsync(export);
        Assert.IsFalse(text.Contains("saved-axis-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("saved-dmdata-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("saved-obs-secret", StringComparison.Ordinal));

        AppSettings current = AppSettings.CreateDefault() with
        {
            Provider = AppSettings.CreateDefault().Provider with
            {
                DmdataProtectedCredential = "current-dmdata",
                AxisProtectedAccessToken = "current-axis",
            },
            Obs = AppSettings.CreateDefault().Obs with { WebSocketProtectedPassword = "current-obs" },
        };
        SettingsProfileDocument loaded = await store.LoadAsync("production", current);
        Assert.AreEqual("current-axis", loaded.Settings.Provider.AxisProtectedAccessToken);
        Assert.AreEqual("current-dmdata", loaded.Settings.Provider.DmdataProtectedCredential);
        Assert.AreEqual("current-obs", loaded.Settings.Obs.WebSocketProtectedPassword);
    }

    [TestMethod]
    public async Task LegacySettingsJsonCanBeImportedAsProfile()
    {
        var store = new JsonSettingsProfileStore(Path.Combine(_directory, "profiles"));
        AppSettings legacy = AppSettings.CreateDefault() with
        {
            SchemaVersion = 20,
            Operations = OperationalSettings.Default,
        };
        string path = Path.Combine(_directory, "旧運用設定.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy));

        SettingsProfileDocument imported = await store.ImportAsync(path, AppSettings.CreateDefault());

        Assert.AreEqual(SettingsProfileDocument.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, imported.Settings.SchemaVersion);
        Assert.AreEqual("旧運用設定", imported.Name);
        Assert.IsTrue(imported.MigrationIssues.Count >= 1);
        Assert.IsTrue(imported.MigrationIssues.Any(static issue => issue.Contains("移行", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TestLibraryRejectsBrokenPayloadAndCopiesValidXml()
    {
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "library"));
        string broken = Path.Combine(_directory, "broken.xml");
        await File.WriteAllTextAsync(broken, "<Report>");
        await Assert.ThrowsExactlyAsync<System.Xml.XmlException>(() =>
            library.ImportFilesAsync("broken", [broken]));

        string valid = Path.Combine(_directory, "sample_VXSE53_010000.xml");
        await File.WriteAllTextAsync(valid, "<Report><Control><Title>震源・震度に関する情報</Title></Control></Report>");
        TestCaseManifest manifest = await library.ImportFilesAsync("valid", [valid]);
        Assert.AreEqual(1, library.List().Count);
        Assert.AreEqual(1, manifest.SchemaVersion);
        File.Delete(valid);
        Assert.AreEqual(1, library.LoadMessages(manifest.Id, EEWTelop.Domain.Events.SourceMode.HistoryRehearsal).Count);
    }

    [TestMethod]
    public async Task TestLibraryStillReadsOldDmdataCasesWithReferenceImages()
    {
        string root = Path.Combine(_directory, "library");
        var library = new FileTestCaseLibrary(root);
        string xml = Path.Combine(_directory, "sample_VXSE53.xml");
        string json = Path.Combine(_directory, "reference.json");
        await File.WriteAllTextAsync(xml, "<Report />");
        await File.WriteAllTextAsync(json, "{}");
        TestCaseManifest original = await library.ImportFilesAsync("旧dmdataケース", [xml, json]);
        string originalFolder = Path.Combine(root, original.Id);
        string referencePath = Path.Combine(originalFolder, "reference.png");
        await File.WriteAllBytesAsync(referencePath, [0]);
        string manifestPath = Path.Combine(originalFolder, "manifest.json");
        var legacyManifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        legacyManifest["provider"] = FileTestCaseLibrary.DmdataTestProviderName;
        legacyManifest["referenceImageFile"] = "reference.png";
        await File.WriteAllTextAsync(manifestPath, legacyManifest.ToJsonString());

        RawProviderMessage message = library.LoadMessages(original.Id, SourceMode.HistoryRehearsal).Single();
        Assert.AreEqual(RawProviderContentFormat.JmaXml, message.ContentFormat);
        Assert.AreEqual("<Report />", message.Payload);
        TestCaseManifest copy = await library.DuplicateAsync(original.Id);
        Assert.HasCount(2, copy.PayloadFiles);
        Assert.IsFalse(File.Exists(Path.Combine(root, copy.Id, "reference.png")));
        Assert.IsTrue(File.Exists(referencePath));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.ImportFilesAsync("画像", [xml, referencePath]));
        Assert.HasCount(2, library.List());
    }

    [TestMethod]
    public async Task TestLibraryRunsEveryLegacyVphw50RawXmlThroughDisplayPipeline()
    {
        string? sample = FindOptionalRepositoryFile(
            "archive",
            "not-for-public-release",
            "test-fixtures",
            "jma-xml",
            "VPHW50",
            "19_10_03_250630_VPHW50.xml");
        if (sample is null)
        {
            Assert.Inconclusive("Local non-public JMA XML archive is not available.");
            return;
        }
        string[] sources = Directory.GetFiles(
            Path.GetDirectoryName(sample)!,
            "*VPHW50.xml",
            SearchOption.TopDirectoryOnly);
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "library"));
        Assert.IsGreaterThanOrEqualTo(7, sources.Length);

        for (int index = 0; index < sources.Length; index++)
        {
            string source = sources[index];
            TestCaseManifest manifest = await library.ImportFilesAsync(
                Path.GetFileNameWithoutExtension(source),
                [source]);
            if (index == 0)
            {
                string manifestPath = Path.Combine(
                    _directory,
                    "library",
                    manifest.Id,
                    "manifest.json");
                await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest with { TelegramType = "250630" }));
                manifest = library.List().Single(item => item.Id == manifest.Id);
            }
            RawProviderMessage message = library.LoadMessages(
                manifest.Id,
                SourceMode.HistoryRehearsal).Single();
            AppSettings settings = AppSettings.CreateDefault();
            var clock = new SystemClock();
            var pipeline = new EventIngestionPipeline(
                new JmaXmlEventNormalizer(new EventSignatureBuilder()),
                new EventVersionCache(),
                new PageComposer(),
                new PriorityCoordinator(clock, settings.Display),
                settings.Display,
                settings.Filter);

            EventIngestionResult result = pipeline.Process(message);

            Assert.AreEqual("VPHW50", manifest.TelegramType, Path.GetFileName(source));
            Assert.AreEqual(EventIngestionStatus.Accepted, result.Status, Path.GetFileName(source));
            WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(
                result.Event,
                Path.GetFileName(source));
            Assert.AreEqual("VPHW50", weather.Issue.RawType, Path.GetFileName(source));
            Assert.AreEqual(
                WeatherInformationType.TornadoAdvisory,
                weather.InformationType,
                Path.GetFileName(source));
            Assert.IsGreaterThan(0, weather.Items.Count, Path.GetFileName(source));
            Assert.IsNotNull(result.Program, Path.GetFileName(source));
            Assert.IsGreaterThan(0, result.Program.Pages.Count, Path.GetFileName(source));
            StringAssert.Contains(
                result.Program.Pages[0].AccessibleText,
                "竜巻注意情報",
                Path.GetFileName(source));
        }
    }

    [TestMethod]
    public async Task BundledVyse50SampleKeepsTheAudienceAndSafetyInstructionOnTheSamePage()
    {
        string? sample = FindOptionalRepositoryFile("jmaxml_20261118_CDI-Telopper", "地震情報", "VYSE50",
            "74_01_01_200512_VYSE50.xml");
        Assert.IsNotNull(sample);
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "nankai-sample"));
        TestCaseManifest manifest = await library.ImportFilesAsync("調査中", [sample]);
        RawProviderMessage message = library.LoadMessages(manifest.Id, SourceMode.HistoryRehearsal).Single();
        AppSettings settings = AppSettings.CreateDefault();
        var pipeline = new EventIngestionPipeline(
            new JmaXmlEventNormalizer(new EventSignatureBuilder()), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new SystemClock(), settings.Display), settings.Display, settings.Filter);

        EventIngestionResult result = pipeline.Process(message);

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        Assert.IsNotNull(result.Program);
        Assert.HasCount(3, result.Program.Pages);
        Assert.EndsWith("調査を開始しました。", result.Program.Pages[1].Blocks
            .Single(block => block.StyleToken == DisplayStyleTokens.Advisory).PrimaryText);
        Assert.AreEqual("南海トラフ地震で被害が想定される地域の方は、個々の状況に応じて、身の安全を守る行動を取ってください。",
            result.Program.Pages[2].Blocks.Single(block => block.StyleToken == DisplayStyleTokens.Advisory).PrimaryText);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BundledVxse53KeepsIntensitySevenForKuriharaWhenFilteringStations(bool hideNonJmaStations)
    {
        string sample = FindRepositoryFile("jmaxml_20261118_CDI-Telopper", "地震情報", "VXSE53",
            "32-39_11_05_240613_VXSE53.xml");
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "intensity-seven-sample"));
        TestCaseManifest manifest = await library.ImportFilesAsync("震度7", [sample]);
        RawProviderMessage message = library.LoadMessages(manifest.Id, SourceMode.HistoryRehearsal).Single();
        AppSettings settings = AppSettings.CreateDefault();
        DisplaySettings display = settings.Display with { HideNonJmaIntensityStations = hideNonJmaStations };
        var pipeline = new EventIngestionPipeline(
            new JmaXmlEventNormalizer(new EventSignatureBuilder()), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new SystemClock(), display), display, settings.Filter);

        EventIngestionResult result = pipeline.Process(message);

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        var quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        QuakePoint station = quake.Points.Single(point => point.StationCode == "0421320");
        Assert.AreEqual("栗原市築館＊", station.Address);
        Assert.AreEqual(JmaScale.Seven, station.Scale);
        Assert.AreEqual(JmaScale.Seven, station.MunicipalityMaximumScale);
        Assert.IsTrue(station.IsNonJmaIntensityStation);
        Assert.IsNotNull(result.Program);
        Assert.AreEqual(SourceMode.HistoryRehearsal, result.Program.SourceMode);
        DisplayBlock[] intensitySeven = result.Program.Pages.SelectMany(page => page.Blocks)
            .Where(block => block.StyleToken == DisplayStyleTokens.Intensity && block.Badge == "震度7").ToArray();
        Assert.HasCount(1, intensitySeven);
        Assert.AreEqual("宮城県栗原市", intensitySeven[0].PrimaryText);
    }

    [TestMethod]
    public async Task BundledForeignEarthquakeDisplaysAllOverseasTsunamiObservationsInSourceOrder()
    {
        string? sample = FindOptionalRepositoryFile("jmaxml_20261118_CDI-Telopper", "地震情報", "VXSE53",
            "32-39_05_06_100831_VXSE53.xml");
        Assert.IsNotNull(sample);
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "foreign-tsunami-sample"));
        TestCaseManifest manifest = await library.ImportFilesAsync("遠地地震", [sample]);
        RawProviderMessage message = library.LoadMessages(manifest.Id, SourceMode.HistoryRehearsal).Single();
        AppSettings settings = AppSettings.CreateDefault();
        var pipeline = new EventIngestionPipeline(
            new JmaXmlEventNormalizer(new EventSignatureBuilder()), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new SystemClock(), settings.Display), settings.Display, settings.Filter);

        EventIngestionResult result = pipeline.Process(message);

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        Assert.IsNotNull(result.Program);
        Assert.AreEqual(SourceMode.HistoryRehearsal, result.Program.SourceMode);
        DisplayPage[] pages = result.Program.Pages.Where(page => page.Blocks.Any(IsObservation)).ToArray();
        Assert.HasCount(5, pages);
        Assert.IsTrue(pages.All(page => page.Blocks[0].PrimaryText == "海外の津波観測"));
        CollectionAssert.AreEqual(OverseasTsunamiSampleRowsPerPage,
            pages.Select(page => page.Blocks.Count(IsObservation)).ToArray());
        CollectionAssert.AreEqual(
            OverseasTsunamiSampleObservations,
            pages.SelectMany(page => page.Blocks).Where(IsObservation)
                .Select(block => $"{block.PrimaryText}|{block.SecondaryText}").ToArray());
        string attribution = string.Concat(result.Program.Pages
            .Skip(pages[0].Index).Take(pages[1].Index - pages[0].Index - 1)
            .SelectMany(page => page.Blocks).Select(block => block.PrimaryText));
        StringAssert.Contains(attribution, "太平洋津波警報センター");
        string lastPageText = string.Concat(result.Program.Pages[^1].Blocks.Select(block => block.PrimaryText));
        StringAssert.Contains(lastPageText, "津波情報を発表しています");

        static bool IsObservation(DisplayBlock block) => block.StyleToken == DisplayStyleTokens.OverseasTsunamiObservation;
    }

    [TestMethod]
    [DataRow("32-39_11_13_250206_VTSE41.xml")]
    [DataRow("32-39_12_13_250206_VTSE41.xml")]
    public async Task TestLibraryRehearsesBundledVtse41ReleaseAfterTheOriginalValidityPeriod(string fileName)
    {
        string sample = FindRepositoryFile("jmaxml_20261118_CDI-Telopper", "津波情報", "VTSE41", fileName);
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "tsunami-release"));
        TestCaseManifest manifest = await library.ImportFilesAsync("津波解除", [sample]);
        RawProviderMessage message = library.LoadMessages(manifest.Id, SourceMode.HistoryRehearsal).Single() with
        {
            ReceivedAt = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero),
        };
        AppSettings settings = AppSettings.CreateDefault();
        var pipeline = new EventIngestionPipeline(
            new JmaXmlEventNormalizer(new EventSignatureBuilder()), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new SystemClock(), settings.Display), settings.Display, settings.Filter);

        EventIngestionResult result = pipeline.Process(message);

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        TsunamiEvent tsunami = Assert.IsInstanceOfType<TsunamiEvent>(result.Event);
        Assert.AreEqual("VTSE41", tsunami.Issue.RawType);
        Assert.AreEqual(SourceMode.HistoryRehearsal, tsunami.SourceMode);
        Assert.AreEqual(message.ReceivedAt, tsunami.ReceivedAt);
        Assert.IsTrue(tsunami.ExpireAt > tsunami.IssuedAt && tsunami.ExpireAt < message.ReceivedAt);
        Assert.IsTrue(tsunami.IsCancelled);
        Assert.IsNotNull(result.Program, result.DisplaySuppressionReason);
        Assert.AreEqual(SourceMode.HistoryRehearsal, result.Program.SourceMode);
        Assert.AreEqual(EndPolicy.AutoHide, result.Program.EndPolicy);
        DisplayPage page = result.Program.Pages.Single();
        Assert.AreEqual(DisplayStyleTokens.TsunamiCancel, page.Blocks[0].StyleToken);
        StringAssert.Contains(page.AccessibleText, "解除されました");
    }

    [TestMethod]
    public async Task BundledRiverFloodContinuesAtTheNextSentenceWithTheSameStationBadge()
    {
        string sample = FindRepositoryFile("jmaxml_20261118_CDI-Telopper", "気象情報", "VXKOXX",
            "16_06_01_220728_VXKOii.xml");
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "river-flood-sample"));
        TestCaseManifest manifest = await library.ImportFilesAsync("指定河川洪水予報", [sample]);
        RawProviderMessage raw = library.LoadMessages(manifest.Id, SourceMode.HistoryRehearsal).Single();
        AppSettings settings = AppSettings.CreateDefault();
        var pipeline = new EventIngestionPipeline(
            new JmaXmlEventNormalizer(new EventSignatureBuilder()), new EventVersionCache(), new PageComposer(),
            new PriorityCoordinator(new SystemClock(), settings.Display), settings.Display, settings.Filter);

        EventIngestionResult result = pipeline.Process(raw);

        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        WeatherWarningEvent weather = Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.IsNotNull(weather.RiverFlood);
        Assert.IsNotNull(result.Program);
        DisplayBlock[] stationPages = result.Program.Pages.Select(page => page.Blocks[0])
            .Where(block => block.Badge == "○○川上流｜警戒レベル３相当").ToArray();
        Assert.HasCount(2, stationPages);
        Assert.EndsWith("「避難判断水位」に到達しました。", stationPages[0].PrimaryText);
        Assert.StartsWith("今後、水位はさらに上昇する見込みです。", stationPages[1].PrimaryText);
        Assert.IsTrue(stationPages.All(block => block.StyleToken == DisplayStyleTokens.WeatherWarning));
        string original = weather.RiverFlood.MainTexts.Single(text => text.StartsWith("【警戒レベル３相当】", StringComparison.Ordinal));
        Assert.AreEqual(original, string.Concat(stationPages.Select(block => block.PrimaryText)));
    }

    [TestMethod]
    public async Task TestLibraryRunsEveryVyse50RawXmlThroughDisplayPipeline()
    {
        string? sample = FindOptionalRepositoryFile(
            "archive",
            "not-for-public-release",
            "test-fixtures",
            "jma-xml",
            "VYSE50",
            "74_01_01_200512_VYSE50.xml");
        if (sample is null)
        {
            Assert.Inconclusive("Local non-public JMA XML archive is not available.");
            return;
        }
        string[] sources = Directory.GetFiles(
            Path.GetDirectoryName(sample)!,
            "*VYSE50.xml",
            SearchOption.TopDirectoryOnly);
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "nankai-library"));
        Assert.IsGreaterThanOrEqualTo(8, sources.Length);

        foreach (string source in sources)
        {
            TestCaseManifest manifest = await library.ImportFilesAsync(
                Path.GetFileNameWithoutExtension(source),
                [source]);
            RawProviderMessage message = library.LoadMessages(
                manifest.Id,
                SourceMode.HistoryRehearsal).Single();
            AppSettings settings = AppSettings.CreateDefault();
            var pipeline = new EventIngestionPipeline(
                new JmaXmlEventNormalizer(new EventSignatureBuilder()),
                new EventVersionCache(),
                new PageComposer(),
                new PriorityCoordinator(new SystemClock(), settings.Display),
                settings.Display,
                settings.Filter);

            EventIngestionResult result = pipeline.Process(message);

            Assert.AreEqual("VYSE50", manifest.TelegramType, Path.GetFileName(source));
            Assert.AreEqual(EventIngestionStatus.Accepted, result.Status, Path.GetFileName(source));
            QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(
                result.Event,
                Path.GetFileName(source));
            Assert.AreEqual("VYSE50", quake.Issue.RawType, Path.GetFileName(source));
            Assert.AreEqual(
                QuakeIssueType.NankaiTroughTemporaryInformation,
                quake.IssueType,
                Path.GetFileName(source));
            Assert.IsNotNull(result.Program, Path.GetFileName(source));
            Assert.IsGreaterThan(0, result.Program.Pages.Count, Path.GetFileName(source));
            StringAssert.Contains(
                result.Program.Pages[0].AccessibleText,
                "南海トラフ地震臨時情報",
                Path.GetFileName(source));
        }
    }

    [TestMethod]
    public void EveryVxse45ForecastTelegramKeepsOnlyWarningsAndTheirCancellation()
    {
        string? sample = FindOptionalRepositoryFile(
            "archive",
            "not-for-public-release",
            "test-fixtures",
            "jma-xml",
            "VXSE45",
            "77_01_01_240613_VXSE45.xml");
        if (sample is null)
        {
            Assert.Inconclusive("Local non-public JMA XML archive is not available.");
            return;
        }
        string[] sources = Directory.GetFiles(
                Path.GetDirectoryName(sample)!,
                "*VXSE45.xml",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.HasCount(33, sources);
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        DisplaySettings display = AppSettings.CreateDefault().Display;

        for (int index = 0; index < sources.Length; index++)
        {
            string source = sources[index];
            NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
                "test-library-jma-xml",
                File.ReadAllText(source),
                SourceMode.HistoryRehearsal,
                new DateTimeOffset(2024, 4, 17, 14, 18, 0, TimeSpan.Zero))
            {
                ContentFormat = RawProviderContentFormat.JmaXml,
            });

            if (index < 3)
            {
                Assert.AreEqual(NormalizeStatus.Ignored, result.Status, Path.GetFileName(source));
                Assert.IsNull(result.Event, Path.GetFileName(source));
                continue;
            }

            Assert.AreEqual(NormalizeStatus.Success, result.Status, Path.GetFileName(source));
            EewEvent eew = Assert.IsInstanceOfType<EewEvent>(
                result.Event,
                Path.GetFileName(source));
            Assert.AreEqual("VXSE45", eew.Issue.RawType, Path.GetFileName(source));
            Assert.IsTrue(eew.IsWarning, Path.GetFileName(source));

            if (index == sources.Length - 1)
            {
                Assert.IsTrue(eew.IsCancelled, Path.GetFileName(source));
                Assert.IsEmpty(eew.Areas, Path.GetFileName(source));
            }
            else
            {
                Assert.IsFalse(eew.IsCancelled, Path.GetFileName(source));
                Assert.IsNotEmpty(eew.Areas, Path.GetFileName(source));
                Assert.IsTrue(
                    eew.Areas.All(static area => area.WarningKind != EewWarningKind.Unknown),
                    Path.GetFileName(source));
            }

            DisplayProgram program = new PageComposer().Compose(eew, display);
            Assert.IsGreaterThan(0, program.Pages.Count, Path.GetFileName(source));
        }
    }

    [TestMethod]
    public async Task TestLibraryDeleteAllRemovesEveryRegisteredCase()
    {
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "library"));
        string first = Path.Combine(_directory, "first.xml");
        string second = Path.Combine(_directory, "second.xml");
        await File.WriteAllTextAsync(first, "<Report><Control><Title>震度速報</Title></Control></Report>");
        await File.WriteAllTextAsync(second, "<Report><Control><Title>津波警報・注意報・予報</Title></Control></Report>");
        await library.ImportFilesAsync("first", [first]);
        await library.ImportFilesAsync("second", [second]);
        Assert.AreEqual(2, library.List().Count);

        await library.DeleteAllAsync();

        Assert.AreEqual(0, library.List().Count);
    }

    [TestMethod]
    public async Task XmlFolderImportCreatesOneCasePerXmlAndReportsUnreadableFiles()
    {
        string folder = Path.Combine(_directory, "folder-import");
        string nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        string bulletin = Path.Combine(nested, "sample_vpbs50.XML");
        await File.WriteAllTextAsync(bulletin,
            "<Report xmlns='http://xml.kishou.go.jp/jmaxml1/'><Control><Title>気象防災速報</Title></Control>" +
            "<Head xmlns='http://xml.kishou.go.jp/jmaxml1/informationBasis1/'><EventID>20261125000000</EventID></Head></Report>");
        await File.WriteAllTextAsync(Path.Combine(folder, "quake.xml"), "<Report><Control><Title>震度速報</Title></Control></Report>");
        await File.WriteAllTextAsync(Path.Combine(folder, "other.xml"), "<Report><Control><Title>その他の情報</Title></Control></Report>");
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.xml"), "<Report>");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.json"), "{}");
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "library"));

        TestXmlFolderImportResult result = await library.ImportXmlFolderAsync(folder);

        Assert.AreEqual(3, result.ImportedCount);
        Assert.AreEqual("broken.xml", result.SkippedFiles.Single());
        var manifests = library.List().ToDictionary(static item => item.Name);
        Assert.AreEqual("地震情報", manifests["quake"].Category);
        Assert.AreEqual("未分類", manifests["other"].Category);
        TestCaseManifest weather = manifests["sample_vpbs50"];
        Assert.AreEqual("気象情報", weather.Category);
        Assert.AreEqual("VPBS50", weather.TelegramType);
        Assert.AreEqual("20261125000000", weather.EventId);
        Assert.HasCount(1, weather.PayloadFiles);
        File.Delete(bulletin);
        RawProviderMessage message = library.LoadMessages(weather.Id, SourceMode.HistoryRehearsal).Single();
        Assert.AreEqual(SourceMode.HistoryRehearsal, message.SourceMode);
        Assert.AreEqual(RawProviderContentFormat.JmaXml, message.ContentFormat);
    }

    [TestMethod]
    public void TestLibraryUiOffersBulkDeleteButNotBulkRun()
    {
        string xamlPath = FindRepositoryFile("src", "EEWTelop.Wpf", "ControlWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        StringAssert.Contains(xaml, "Content=\"一括削除\"");
        StringAssert.Contains(xaml, "Command=\"{Binding ImportTestXmlFolderCommand}\"");
        StringAssert.Contains(xaml, "SelectedItem=\"{Binding SelectedTestLibraryKind}\"");
        StringAssert.Contains(xaml, "SelectedItem=\"{Binding SelectedTestLibraryTelegramType}\"");
        Assert.IsFalse(xaml.Contains("Content=\"一括実行\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("RunAllLibraryCasesCommand", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("期待する種別／状態", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("メタデータと期待結果を保存", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("SaveLibraryCaseCommand", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApplicationPreventsMultipleProcessesAndRestoresTheRunningWindow()
    {
        string appPath = FindRepositoryFile("src", "EEWTelop.Wpf", "App.xaml.cs");
        string coordinatorPath = FindRepositoryFile(
            "src", "EEWTelop.Wpf", "SingleInstanceCoordinator.cs");
        string app = File.ReadAllText(appPath);
        string coordinator = File.ReadAllText(coordinatorPath);

        StringAssert.Contains(app, "if (!_singleInstanceCoordinator.IsPrimaryInstance)");
        StringAssert.Contains(app, "_singleInstanceCoordinator.NotifyPrimaryInstance()");
        StringAssert.Contains(app, "Dispatcher.BeginInvoke((Action)RestoreControlWindow)");
        StringAssert.Contains(coordinator, "Local\\QTelopper.SingleInstance");
        StringAssert.Contains(coordinator, "EventResetMode.AutoReset");
        StringAssert.Contains(coordinator, "ThreadPool.RegisterWaitForSingleObject");
    }

    [TestMethod]
    public void TrayMenuOpensAnIndependentTelegramReviewWindow()
    {
        string appPath = FindRepositoryFile("src", "EEWTelop.Wpf", "App.xaml.cs");
        string controlWindowPath = FindRepositoryFile(
            "src", "EEWTelop.Wpf", "ControlWindow.xaml.cs");
        string reviewWindowPath = FindRepositoryFile(
            "src", "EEWTelop.Wpf", "TelegramReviewWindow.xaml");
        string app = File.ReadAllText(appPath);
        string controlWindow = File.ReadAllText(controlWindowPath);
        string reviewWindow = File.ReadAllText(reviewWindowPath);

        StringAssert.Contains(app, "\"受信・過去電文を確認\"");
        StringAssert.Contains(app, "OpenTelegramReviewWindow()");
        StringAssert.Contains(app, "_controlWindow?.ShowTelegramReviewWindow()");
        StringAssert.Contains(controlWindow, "internal void ShowTelegramReviewWindow()");
        StringAssert.Contains(controlWindow, "new TelegramReviewWindow(_viewModel)");
        Assert.IsFalse(controlWindow.Contains(
            "new TelegramReviewWindow(_viewModel) { Owner = this }",
            StringComparison.Ordinal));
        StringAssert.Contains(reviewWindow, "WindowStartupLocation=\"CenterScreen\"");
    }

    [TestMethod]
    public void ProductIdentityUsesCdiTelopperWhileKeepingCompatibilityStorage()
    {
        string project = File.ReadAllText(FindRepositoryFile(
            "src", "EEWTelop.Wpf", "EEWTelop.Wpf.csproj"));
        string controlWindow = File.ReadAllText(FindRepositoryFile(
            "src", "EEWTelop.Wpf", "ControlWindow.xaml"));
        string composition = File.ReadAllText(FindRepositoryFile(
            "src", "EEWTelop.Wpf", "Bootstrap", "AppComposition.cs"));
        string obsSynchronizer = File.ReadAllText(FindRepositoryFile(
            "src", "EEWTelop.Wpf", "Obs", "ObsBrowserSourceSynchronizer.cs"));
        string publishScript = File.ReadAllText(FindRepositoryFile(
            "scripts", "publish.ps1"));

        StringAssert.Contains(project, "<AssemblyName>CDI-Telopper</AssemblyName>");
        StringAssert.Contains(project,
            "<Title>Comprehensive Disaster Information Telopper</Title>");
        StringAssert.Contains(controlWindow, "Text=\"CDI-Telopper\"");
        StringAssert.Contains(controlWindow,
            "Text=\"Comprehensive Disaster Information Telopper\"");
        StringAssert.Contains(obsSynchronizer,
            "GeneralSourceName = \"CDI-Telopper 地震字幕\"");
        StringAssert.Contains(obsSynchronizer,
            "(\"CDI-Telopper 地震字幕・全ての音声\", GeneralSourceName)");
        StringAssert.Contains(obsSynchronizer,
            "(\"QTelopper 地震字幕・全ての音声\", GeneralSourceName)");
        StringAssert.Contains(publishScript, "'CDI-Telopper.exe'");
        StringAssert.Contains(publishScript,
            "CDI-Telopper-$Version-$RuntimeIdentifier-single-file.zip");

        // 旧版の設定・暗号化資格情報をそのまま引き継ぐため保存先識別子は維持する。
        StringAssert.Contains(composition,
            "Path.Combine(localApplicationData, \"QTelopper\", dataSeries)");
    }

    [TestMethod]
    public void MainHeaderUsesTransparentIconBesideProductName()
    {
        string controlWindow = File.ReadAllText(FindRepositoryFile(
            "src", "EEWTelop.Wpf", "ControlWindow.xaml"));

        StringAssert.Contains(controlWindow, "Source=\"Assets/CDI-Telopper.png\"");
        StringAssert.Contains(controlWindow, "Text=\"CDI-Telopper\"");
        Assert.IsFalse(controlWindow.Contains(
            "Source=\"Assets/CDI-Telopper-BrandHeader.png\"",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void SourceComparisonIncludesSelectedAudioCueWithoutDelayingResult()
    {
        QuakeEvent sample = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow)
            .Single(item => item.Id == "detail-scale").Event;
        QuakeEvent p2p = CopyForProvider(sample, "p2pquake");
        QuakeEvent axis = CopyForProvider(sample, "axis");
        var service = new SourceComparisonService(TimeSpan.FromMinutes(10));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        service.Observe(new RawProviderMessage("p2pquake", "{}", SourceMode.Production, now),
            new EventIngestionResult(EventIngestionStatus.Accepted, p2p, null, null, []), now);
        service.Observe(new RawProviderMessage("axis", "{}", SourceMode.Production, now),
            new EventIngestionResult(EventIngestionStatus.Accepted, axis, null, null, []), now);

        service.ObserveSelectedAudio(p2p, "QuakeThree", now.AddMilliseconds(1));
        service.ObserveSelectedAudio(axis, "QuakeFour", now.AddMilliseconds(2));

        SourceComparisonResult result = service.GetSnapshot(now.AddMilliseconds(3)).Single();
        Assert.AreEqual(SourceComparisonStatus.Different, result.Status);
        Assert.IsTrue(result.Differences.Any(item => item.Contains("音声種別", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SourceComparisonReportsMissingCounterpartOnlyAfterWaitWindow()
    {
        QuakeEvent sample = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow)
            .Single(item => item.Id == "detail-scale").Event;
        QuakeEvent p2p = CopyForProvider(sample, "p2pquake");
        QuakeEvent axis = CopyForProvider(sample, "axis", EventId.Create("other-event"));
        var service = new SourceComparisonService(TimeSpan.FromMinutes(10));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        service.Observe(new RawProviderMessage("axis", "{}", SourceMode.Production, now),
            new EventIngestionResult(EventIngestionStatus.Accepted, axis, null, null, []), now);
        service.Observe(new RawProviderMessage("p2pquake", "{}", SourceMode.Production, now),
            new EventIngestionResult(EventIngestionStatus.Accepted, p2p, null, null, []), now);

        SourceComparisonResult waiting = service.GetSnapshot(now.AddMinutes(9))
            .Single(item => item.CorrelationKey.StartsWith(p2p.Id.Value, StringComparison.Ordinal));
        SourceComparisonResult missing = service.GetSnapshot(now.AddMinutes(11))
            .Single(item => item.CorrelationKey.StartsWith(p2p.Id.Value, StringComparison.Ordinal));

        Assert.AreEqual(SourceComparisonStatus.Waiting, waiting.Status);
        Assert.AreEqual(SourceComparisonStatus.CounterpartMissing, missing.Status);
    }

    private static QuakeEvent CopyForProvider(
        QuakeEvent source,
        string provider,
        EventId? id = null) => new(
        id ?? source.Id, provider, source.IssuedAt, source.ReceivedAt, source.Signature, source.SourceMode,
        source.Issue, source.IssueType, source.Earthquake, source.Points, source.FreeFormComment,
        source.LongPeriodIntensity);

    internal static string FindRepositoryFile(params string[] pathSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. pathSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Repository file was not found: {Path.Combine(pathSegments)}");
        return string.Empty;
    }

    private static string? FindOptionalRepositoryFile(params string[] pathSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. pathSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
