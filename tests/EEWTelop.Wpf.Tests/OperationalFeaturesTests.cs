using System.Text.Json;
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
    public void TestLibraryUiOffersBulkDeleteButNotBulkRun()
    {
        string xamlPath = FindRepositoryFile("src", "EEWTelop.Wpf", "ControlWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        StringAssert.Contains(xaml, "Content=\"一括削除\"");
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
            "\"CDI-Telopper 地震字幕・全ての音声\"");
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
    public async Task DmdataArchiveCreatesOneCasePerOfficialXmlAndKeepsJsonAsReferenceOnly()
    {
        string source = Path.Combine(_directory, "dmdata");
        Directory.CreateDirectory(source);
        string xmlFile = "VXSE43_RJTD_20240101070614227_sample.xml";
        string jsonFile = "VXSE43_RJTD_20240101070614234_sample.json";
        await File.WriteAllTextAsync(Path.Combine(source, xmlFile),
            "<Report><Control><Title>緊急地震速報（警報）</Title></Control><Head><EventID>20240101160608</EventID></Head></Report>");
        await File.WriteAllTextAsync(Path.Combine(source, jsonFile),
            "{\"_originalId\":\"official-xml-1\",\"type\":\"VXSE43\"}");
        object[] index =
        [
            new
            {
                id = "official-xml-1",
                classification = "eew.warning",
                head = new { type = "VXSE43" },
                xmlReport = new { head = new { eventId = "20240101160608", serial = 1 } },
                format = "xml",
                filename = xmlFile,
            },
            new
            {
                id = "converted-json-1",
                originalId = "official-xml-1",
                classification = "eew.warning",
                head = new { type = "VXSE43" },
                xmlReport = new { head = new { eventId = "20240101160608", serial = 1 } },
                format = "json",
                filename = jsonFile,
            },
        ];
        string telegrams = Path.Combine(source, "telegrams.json");
        await File.WriteAllTextAsync(telegrams, JsonSerializer.Serialize(index));
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "library"));

        IReadOnlyList<TestCaseManifest> imported = await library.ImportDmdataArchiveAsync(telegrams);

        Assert.AreEqual(1, imported.Count);
        Assert.AreEqual(FileTestCaseLibrary.DmdataTestProviderName, imported[0].Provider);
        Assert.AreEqual("VXSE43", imported[0].TelegramType);
        Assert.AreEqual("20240101160608", imported[0].EventId);
        Assert.AreEqual(2, imported[0].PayloadFiles.Count);
        IReadOnlyList<RawProviderMessage> messages = library.LoadMessages(
            imported[0].Id, EEWTelop.Domain.Events.SourceMode.HistoryRehearsal);
        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(RawProviderContentFormat.JmaXml, messages[0].ContentFormat);
        Assert.IsTrue(messages[0].Payload.Contains("<Report>", StringComparison.Ordinal));
        Assert.IsFalse(messages[0].Payload.Contains("_originalId", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DmdataArchiveRejectsIndexPathTraversalWithoutLeavingCases()
    {
        string source = Path.Combine(_directory, "dmdata-invalid");
        Directory.CreateDirectory(source);
        object[] index =
        [
            new
            {
                id = "official-xml-1",
                classification = "eew.warning",
                head = new { type = "VXSE43" },
                xmlReport = new { head = new { eventId = "event", serial = 1 } },
                format = "xml",
                filename = "../outside.xml",
            },
        ];
        string telegrams = Path.Combine(source, "telegrams.json");
        await File.WriteAllTextAsync(telegrams, JsonSerializer.Serialize(index));
        var library = new FileTestCaseLibrary(Path.Combine(_directory, "library"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            library.ImportDmdataArchiveAsync(telegrams));

        Assert.AreEqual(0, library.List().Count);
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

    private static string FindRepositoryFile(params string[] pathSegments)
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
