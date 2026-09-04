using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Persistence;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class Phase8PersistenceAndAudioTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AudioPolicyUsesSeparateTsunamiFilesAndAllowsEscalationCue()
    {
        var policy = new AudioPolicy();
        AudioSettings settings = AudioSettings.Disabled with
        {
            TsunamiEnabled = true,
            TsunamiWarningEnabled = true,
            TsunamiMajorWarningEnabled = true,
            TsunamiWarningFilePath = "warning.mp3",
            TsunamiMajorWarningFilePath = "major.mp3",
            Muted = false,
        };
        TsunamiEvent warning = CreateTsunami(TsunamiGrade.Warning, "sig-1");
        TsunamiEvent update = CreateTsunami(TsunamiGrade.Warning, "sig-2");
        TsunamiEvent escalation = CreateTsunami(TsunamiGrade.MajorWarning, "sig-3");

        AudioDecision first = policy.Evaluate(warning, settings);
        AudioDecision duplicateCue = policy.Evaluate(update, settings);
        AudioDecision major = policy.Evaluate(escalation, settings);

        Assert.IsTrue(first.ShouldPlay);
        Assert.AreEqual(AudioCueId.TsunamiWarning, first.Cue);
        Assert.AreEqual("warning.mp3", first.FilePath);
        Assert.IsFalse(duplicateCue.ShouldPlay);
        Assert.IsTrue(major.ShouldPlay);
        Assert.AreEqual(AudioCueId.TsunamiMajorWarning, major.Cue);
        Assert.AreEqual("major.mp3", major.FilePath);
    }

    [TestMethod]
    public void TsunamiAudioUsesTheFileForTheHighestPublishedGrade()
    {
        var policy = new AudioPolicy();
        AudioSettings settings = AudioSettings.Disabled with
        {
            TsunamiEnabled = true,
            TsunamiAdvisoryEnabled = true,
            TsunamiWarningEnabled = true,
            TsunamiMajorWarningEnabled = true,
            TsunamiAdvisoryFilePath = "advisory.wav",
            TsunamiWarningFilePath = "warning.wav",
            TsunamiMajorWarningFilePath = "major.wav",
        };

        AudioDecision watch = policy.Evaluate(
            CreateTsunami(TsunamiGrade.Watch, "watch", "tsunami-watch"),
            settings);
        AudioDecision warning = policy.Evaluate(
            CreateTsunami(TsunamiGrade.Warning, "warning", "tsunami-warning"),
            settings);

        Assert.IsTrue(watch.ShouldPlay);
        Assert.AreEqual(AudioCueId.TsunamiAdvisory, watch.Cue);
        Assert.AreEqual("advisory.wav", watch.FilePath);
        Assert.IsTrue(warning.ShouldPlay);
        Assert.AreEqual(AudioCueId.TsunamiWarning, warning.Cue);
        Assert.AreEqual("warning.wav", warning.FilePath);

        TsunamiEvent mixedGrades = CreateTsunami(
            TsunamiGrade.Watch,
            "mixed",
            "tsunami-mixed",
            TsunamiGrade.Warning,
            TsunamiGrade.MajorWarning);
        AudioDecision major = policy.Evaluate(mixedGrades, settings);

        Assert.IsTrue(major.ShouldPlay);
        Assert.AreEqual(AudioCueId.TsunamiMajorWarning, major.Cue);
        Assert.AreEqual("major.wav", major.FilePath);
    }

    [TestMethod]
    public void QuakeAudioHonorsTheSelectedMinimumIntensity()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(Now);
        QuakeEvent intensityFour = Assert.IsInstanceOfType<QuakeEvent>(
            scenarios.Single(item => item.Id == "large-4").Event);
        QuakeEvent intensityFiveLower = Assert.IsInstanceOfType<QuakeEvent>(
            scenarios.Single(item => item.Id == "large-5-lower").Event);
        var policy = new AudioPolicy();
        AudioSettings settings = AudioSettings.Disabled with
        {
            QuakeEnabled = true,
            QuakeFilePath = "quake.wav",
            TestUsesProductionSound = true,
            MinimumQuakeScale = JmaScale.FiveLower,
        };

        Assert.IsFalse(policy.Evaluate(intensityFour, settings).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(intensityFiveLower, settings).ShouldPlay);
    }

    [TestMethod]
    public void TsunamiAudioUsesForecastAdvisoryWarningAndMajorWarningCuesIndependently()
    {
        AudioSettings settings = AudioSettings.Disabled with
        {
            TsunamiGradeCues = new Dictionary<TsunamiGrade, AudioCueSetting>
            {
                [TsunamiGrade.Forecast] = new(true, "forecast.wav"),
                [TsunamiGrade.Watch] = new(true, "advisory.wav"),
                [TsunamiGrade.Warning] = new(true, "warning.wav"),
                [TsunamiGrade.MajorWarning] = new(true, "major.wav"),
            },
        };
        var policy = new AudioPolicy();

        AudioDecision forecast = policy.Evaluate(
            CreateTsunami(TsunamiGrade.Forecast, "forecast", "forecast"), settings);
        AudioDecision advisory = policy.Evaluate(
            CreateTsunami(TsunamiGrade.Watch, "advisory", "advisory"), settings);
        AudioDecision warning = policy.Evaluate(
            CreateTsunami(TsunamiGrade.Warning, "warning-2", "warning-2"), settings);
        AudioDecision major = policy.Evaluate(
            CreateTsunami(TsunamiGrade.MajorWarning, "major-2", "major-2"), settings);

        Assert.AreEqual(AudioCueId.TsunamiForecast, forecast.Cue);
        Assert.AreEqual("forecast.wav", forecast.FilePath);
        Assert.AreEqual(AudioCueId.TsunamiAdvisory, advisory.Cue);
        Assert.AreEqual(AudioCueId.TsunamiWarning, warning.Cue);
        Assert.AreEqual(AudioCueId.TsunamiMajorWarning, major.Cue);
    }

    [TestMethod]
    public void QuakeAudioUsesTheExactObservedIntensityCue()
    {
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(Now);
        QuakeEvent intensityFour = Assert.IsInstanceOfType<QuakeEvent>(
            scenarios.Single(item => item.Id == "large-4").Event);
        QuakeEvent intensityFiveLower = Assert.IsInstanceOfType<QuakeEvent>(
            scenarios.Single(item => item.Id == "large-5-lower").Event);
        AudioSettings settings = AudioSettings.Disabled with
        {
            TestUsesProductionSound = true,
            QuakeScaleCues = new Dictionary<JmaScale, AudioCueSetting>
            {
                [JmaScale.Four] = new(true, "four.wav"),
                [JmaScale.FiveLower] = new(true, "five-lower.wav"),
            },
        };
        var policy = new AudioPolicy();

        AudioDecision four = policy.Evaluate(intensityFour, settings);
        AudioDecision fiveLower = policy.Evaluate(intensityFiveLower, settings);

        Assert.AreEqual(AudioCueId.QuakeIntensity4, four.Cue);
        Assert.AreEqual("four.wav", four.FilePath);
        Assert.AreEqual(AudioCueId.QuakeIntensity5Lower, fiveLower.Cue);
        Assert.AreEqual("five-lower.wav", fiveLower.FilePath);
    }

    [TestMethod]
    public void DisabledExactQuakeIntensityDoesNotFallBackToAnotherCue()
    {
        QuakeEvent intensityFour = Assert.IsInstanceOfType<QuakeEvent>(
            TestScenarioCatalog.Create(Now).Single(item => item.Id == "large-4").Event);
        AudioSettings settings = AudioSettings.Disabled with
        {
            TestUsesProductionSound = true,
            QuakeScaleCues = new Dictionary<JmaScale, AudioCueSetting>
            {
                [JmaScale.Three] = new(true, "three.wav"),
                [JmaScale.Four] = new(false, "four.wav"),
            },
        };

        Assert.IsFalse(new AudioPolicy().Evaluate(intensityFour, settings).ShouldPlay);
    }

    [TestMethod]
    public void TrainingAudioUsesTheConfiguredCategoryOnlyWhenEnabledForRehearsal()
    {
        var policy = new AudioPolicy();
        QuakeEvent quake = (QuakeEvent)TestScenarioCatalog.Create(Now)
            .Single(item => item.Id == "detail-scale").Event;
        AudioSettings settings = AppSettings.CreateDefault().Audio with
        {
            QuakeEnabled = true,
            QuakeFilePath = "quake.wav",
            TestUsesProductionSound = false,
        };

        Assert.IsFalse(policy.Evaluate(quake, settings).ShouldPlay);
        AudioDecision enabled = policy.Evaluate(quake, settings with
        {
            TestUsesProductionSound = true,
        });

        Assert.IsTrue(enabled.ShouldPlay);
        Assert.AreEqual(AudioCueId.QuakeIntensity3OrMore, enabled.Cue);
        Assert.AreEqual("quake.wav", enabled.FilePath);
    }

    [TestMethod]
    public void EewInitialContinuationAndCancellationUseIndependentFiles()
    {
        AudioSettings settings = AppSettings.CreateDefault().Audio with
        {
            EewEnabled = true,
            EewInitialEnabled = true,
            EewContinuationEnabled = true,
            EewCancellationEnabled = true,
            EewInitialFilePath = "initial.wav",
            EewContinuationFilePath = "continuation.mp3",
            EewCancellationFilePath = "cancellation.ogg",
        };
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(Now);
        var policy = new AudioPolicy();

        EewEvent warning = Assert.IsInstanceOfType<EewEvent>(
            scenarios.Single(item => item.Id == "eew-warning").Event);
        var initialEvent = new EewEvent(
            warning.Id,
            warning.Provider,
            warning.IssuedAt,
            warning.ReceivedAt,
            warning.Signature,
            warning.SourceMode,
            warning.Issue with { Serial = "1" },
            warning.Earthquake,
            warning.Areas,
            warning.IsWarning,
            isFinal: false,
            isCancelled: false,
            warning.IsTest);
        AudioDecision initial = policy.Evaluate(initialEvent, settings);
        AudioDecision continuation = policy.Evaluate(
            scenarios.Single(item => item.Id == "eew-warning").Event,
            settings);
        AudioDecision cancellation = policy.Evaluate(
            scenarios.Single(item => item.Id == "eew-cancel").Event,
            settings);

        Assert.AreEqual(AudioCueId.EewInitial, initial.Cue);
        Assert.AreEqual("initial.wav", initial.FilePath);
        Assert.AreEqual(AudioCueId.EewContinuation, continuation.Cue);
        Assert.AreEqual("continuation.mp3", continuation.FilePath);
        Assert.AreEqual(AudioCueId.EewCancellation, cancellation.Cue);
        Assert.AreEqual("cancellation.ogg", cancellation.FilePath);
    }

    [TestMethod]
    public void WeatherAlertLevelsUseIndependentAudioFiles()
    {
        AudioSettings settings = AudioSettings.Disabled with
        {
            WeatherSpecialWarningEnabled = true,
            WeatherWarningEnabled = true,
            WeatherAdvisoryEnabled = true,
            WeatherSpecialWarningFilePath = "weather-level5.wav",
            WeatherWarningFilePath = "weather-level4-3.mp3",
            WeatherAdvisoryFilePath = "weather-level2.ogg",
        };
        var policy = new AudioPolicy();

        AudioDecision special = policy.Evaluate(
            CreateWeather(WeatherWarningLevel.SpecialWarning, "weather-special"),
            settings);
        AudioDecision warning = policy.Evaluate(
            CreateWeather(WeatherWarningLevel.Warning, "weather-warning"),
            settings);
        AudioDecision advisory = policy.Evaluate(
            CreateWeather(WeatherWarningLevel.Advisory, "weather-advisory"),
            settings);

        Assert.AreEqual(AudioCueId.WeatherSpecialWarning, special.Cue);
        Assert.AreEqual("weather-level5.wav", special.FilePath);
        Assert.AreEqual(AudioCueId.WeatherWarning, warning.Cue);
        Assert.AreEqual("weather-level4-3.mp3", warning.FilePath);
        Assert.AreEqual(AudioCueId.WeatherAdvisory, advisory.Cue);
        Assert.AreEqual("weather-level2.ogg", advisory.FilePath);
    }

    [TestMethod]
    public void EveryWeatherTrainingCategorySelectsTheExpectedAudioBand()
    {
        AudioSettings settings = AudioSettings.Disabled with
        {
            WeatherSpecialWarningEnabled = true,
            WeatherWarningEnabled = true,
            WeatherAdvisoryEnabled = true,
            WeatherSpecialWarningFilePath = "special.wav",
            WeatherWarningFilePath = "warning.wav",
            WeatherAdvisoryFilePath = "advisory.wav",
        };
        var policy = new AudioPolicy();
        IReadOnlyList<TestScenario> scenarios = TestScenarioCatalog.Create(Now);

        (string ScenarioId, AudioCueId Cue)[] expected =
        [
            ("weather-special-warning", AudioCueId.WeatherSpecialWarning),
            ("weather-warning", AudioCueId.WeatherWarning),
            ("weather-advisory", AudioCueId.WeatherAdvisory),
            ("weather-level5", AudioCueId.WeatherSpecialWarning),
            ("weather-level4", AudioCueId.WeatherWarning),
            ("weather-level3", AudioCueId.WeatherWarning),
            ("weather-level2", AudioCueId.WeatherAdvisory),
        ];

        foreach ((string scenarioId, AudioCueId cue) in expected)
        {
            AudioDecision decision = policy.Evaluate(
                scenarios.Single(item => item.Id == scenarioId).Event,
                settings);
            Assert.AreEqual(cue, decision.Cue, scenarioId);
            Assert.IsTrue(decision.ShouldPlay, scenarioId);
        }
    }

    [TestMethod]
    public void WeatherAudioIgnoresReleasesAndUsesDedicatedDisasterBulletinCue()
    {
        AudioSettings settings = AudioSettings.Disabled with
        {
            WeatherWarningEnabled = true,
            WeatherWarningFilePath = "weather-warning.wav",
            WeatherDisasterPreventionBulletinEnabled = true,
            WeatherDisasterPreventionBulletinFilePath = "weather-bulletin.wav",
        };
        var policy = new AudioPolicy();

        AudioDecision release = policy.Evaluate(
            CreateWeather(
                WeatherWarningLevel.Warning,
                "weather-release",
                isCancelled: true),
            settings);
        AudioDecision bulletin = policy.Evaluate(
            CreateWeather(
                WeatherWarningLevel.Warning,
                "weather-bulletin",
                informationType: WeatherInformationType.DisasterPreventionBulletin),
            settings);

        Assert.IsFalse(release.ShouldPlay);
        Assert.IsTrue(bulletin.ShouldPlay);
        Assert.AreEqual(AudioCueId.WeatherDisasterPreventionBulletin, bulletin.Cue);
        Assert.AreEqual("weather-bulletin.wav", bulletin.FilePath);

        AudioDecision disabledBulletin = new AudioPolicy().Evaluate(
            CreateWeather(
                WeatherWarningLevel.Warning,
                "weather-bulletin-disabled",
                informationType: WeatherInformationType.DisasterPreventionBulletin),
            settings with { WeatherDisasterPreventionBulletinEnabled = false });
        Assert.IsFalse(disabledBulletin.ShouldPlay);
    }

    [TestMethod]
    public void WeatherAudioPlaysOnceForEachNewReportOfTheSameEvent()
    {
        AudioSettings settings = AudioSettings.Disabled with
        {
            WeatherWarningEnabled = true,
            WeatherWarningFilePath = "weather-warning.wav",
        };
        var policy = new AudioPolicy();
        WeatherWarningEvent first = CreateWeather(
            WeatherWarningLevel.Warning,
            "weather-event",
            signature: "weather-report-1",
            serial: "1");
        WeatherWarningEvent second = CreateWeather(
            WeatherWarningLevel.Warning,
            "weather-event",
            signature: "weather-report-2",
            serial: "2");
        WeatherWarningEvent duplicateSecond = CreateWeather(
            WeatherWarningLevel.Warning,
            "weather-event",
            signature: "weather-report-2",
            serial: "2");
        WeatherWarningEvent continuationOnly = CreateWeather(
            WeatherWarningLevel.Warning,
            "weather-event",
            signature: "weather-report-3",
            serial: "3",
            status: "継続");

        Assert.IsTrue(policy.Evaluate(first, settings).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(second, settings).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(duplicateSecond, settings).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(continuationOnly, settings).ShouldPlay);
    }

    [TestMethod]
    public void WeatherAudioUsesSignatureWhenTheReportSerialIsMissing()
    {
        AudioSettings settings = AudioSettings.Disabled with
        {
            WeatherAdvisoryEnabled = true,
            WeatherAdvisoryFilePath = "weather-advisory.wav",
        };
        var policy = new AudioPolicy();

        Assert.IsTrue(policy.Evaluate(
            CreateWeather(
                WeatherWarningLevel.Advisory,
                "axis-weather-event",
                signature: "axis-signature-1"),
            settings).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(
            CreateWeather(
                WeatherWarningLevel.Advisory,
                "axis-weather-event",
                signature: "axis-signature-2"),
            settings).ShouldPlay);
    }

    [TestMethod]
    public void RestoreAcceptsCurrentProductionTsunamiAndRejectsRehearsalAndExpiredItems()
    {
        DisplayProgram tsunami = CreateProgram(
            "tsunami",
            EventKind.Tsunami,
            SourceMode.Production,
            Now.AddHours(-1),
            OverlayPriority.TsunamiWarning,
            EndPolicy.LoopUntilReplaced,
            string.Empty);
        DisplayProgram rehearsal = CreateProgram(
            "training",
            EventKind.Eew,
            SourceMode.ManualTest,
            Now.AddSeconds(-5),
            OverlayPriority.Eew,
            EndPolicy.AutoHide,
            "訓練");
        DisplayProgram expiredQuake = CreateProgram(
            "quake",
            EventKind.Quake,
            SourceMode.Production,
            Now.AddMinutes(-11),
            OverlayPriority.Quake,
            EndPolicy.AutoHide,
            string.Empty);
        var document = new DisplayStateDocument(
            DisplayStateDocument.CurrentSchemaVersion,
            Now,
            false,
            StoredDisplayProgram.From(tsunami, Now.AddMinutes(-2)),
            StoredDisplayProgram.From(tsunami, Now.AddMinutes(-2)),
            [
                StoredDisplayProgram.From(rehearsal, Now.AddSeconds(-5)),
                StoredDisplayProgram.From(expiredQuake, Now.AddMinutes(-11)),
            ],
            [],
            null,
            "Production");

        CoordinatorRestoreState restored = document.ToRestoreState(Now);

        Assert.IsNotNull(restored.Current);
        Assert.IsNotNull(restored.PersistentTsunami);
        Assert.AreEqual("tsunami", restored.Current.Program.ProgramId);
        Assert.IsEmpty(restored.Pending);
    }

    [TestMethod]
    public void StateSnapshotNeverPersistsManualTestPrograms()
    {
        DisplayProgram rehearsal = CreateProgram(
            "training",
            EventKind.Eew,
            SourceMode.ManualTest,
            Now,
            OverlayPriority.Eew,
            EndPolicy.AutoHide,
            "訓練");
        var snapshot = new CoordinatorSnapshot(
            rehearsal,
            rehearsal.Pages[0],
            0,
            TimeSpan.Zero,
            Now,
            Now.AddSeconds(45),
            TimeSpan.FromSeconds(45),
            [rehearsal],
            null,
            new CoordinatorDecision(CoordinatorDecisionKind.Activated, "test"),
            false);

        DisplayStateDocument document = DisplayStateDocument.FromSnapshot(
            snapshot,
            Now,
            lastShutdownWasClean: false);

        Assert.IsNull(document.Current);
        Assert.IsNull(document.PersistentTsunami);
        Assert.IsEmpty(document.Pending);
    }

    [TestMethod]
    public void PriorityCoordinatorRestoresAtThePageImpliedByUtcElapsedTime()
    {
        var clock = new FakeClock(Now);
        var coordinator = new PriorityCoordinator(
            clock,
            CoordinatorTestSupport.Settings(pageDurationSeconds: 4));
        DisplayProgram tsunami = CoordinatorTestSupport.Program(
            "restored-tsunami",
            EventKind.Tsunami,
            OverlayPriority.TsunamiWarning,
            issuedAt: Now.AddMinutes(-1),
            pageCount: 3,
            endPolicy: EndPolicy.LoopUntilReplaced);
        var restored = new RestoredProgram(tsunami, Now.AddSeconds(-10));

        CoordinatorSnapshot snapshot = coordinator.Restore(new CoordinatorRestoreState(
            restored,
            restored,
            []));

        Assert.AreEqual(CoordinatorDecisionKind.Restored, snapshot.Decision.Kind);
        Assert.AreEqual("restored-tsunami", snapshot.CurrentProgram?.ProgramId);
        Assert.AreEqual(2, snapshot.CurrentPageIndex);
        Assert.AreEqual("page-2", snapshot.CurrentPage?.AccessibleText);
    }

    private static TsunamiEvent CreateTsunami(
        TsunamiGrade grade,
        string signature,
        string id = "tsunami-a",
        params TsunamiGrade[] additionalGrades) => new(
        EventId.Create(id),
        "test",
        Now,
        Now,
        signature,
        SourceMode.Production,
        new IssueInfo("test", Now, "Detail", CorrectionType.None),
        [
            new TsunamiArea(grade, false, "東京湾内湾", null, null),
            .. additionalGrades.Select((item, index) => new TsunamiArea(
                item,
                false,
                $"追加予報区{index + 1}",
                null,
                null)),
        ],
        isCancelled: false,
        expireAt: null);

    private static WeatherWarningEvent CreateWeather(
        WeatherWarningLevel level,
        string id,
        bool isCancelled = false,
        WeatherInformationType informationType =
            WeatherInformationType.WarningAndAdvisory,
        string? signature = null,
        string? serial = null,
        string status = "発表") => new(
        EventId.Create(id),
        "test",
        Now,
        Now,
        signature ?? id,
        SourceMode.Production,
        new IssueInfo("test", Now, "VPWW55", CorrectionType.None, serial),
        "気象警報・注意報",
        [new WeatherWarningItem(
            "東京都",
            "130000",
            level switch
            {
                WeatherWarningLevel.SpecialWarning => "レベル５大雨特別警報",
                WeatherWarningLevel.Warning => "レベル４大雨危険警報",
                WeatherWarningLevel.Advisory => "レベル２大雨注意報",
                _ => "不明",
            },
            string.Empty,
            level,
            isCancelled ? "解除" : status,
            !isCancelled)],
        isCancelled,
        informationType);

    private static DisplayProgram CreateProgram(
        string id,
        EventKind kind,
        SourceMode sourceMode,
        DateTimeOffset issuedAt,
        OverlayPriority priority,
        EndPolicy endPolicy,
        string rehearsalLabel) => new(
            id,
            EventId.Create(id),
            kind,
            sourceMode,
            issuedAt,
            priority,
            [new DisplayPage(0, [new DisplayBlock("", id, "", "summary")], id, null)],
            issuedAt,
            endPolicy,
            rehearsalLabel);
}
