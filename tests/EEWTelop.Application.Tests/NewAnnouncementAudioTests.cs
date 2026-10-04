using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class NewAnnouncementAudioTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(AudioAnnouncementMode.FirstAnnouncementOnly, false)]
    [DataRow(AudioAnnouncementMode.NewAnnouncementOrEscalation, true)]
    public void QuakeUpdatesShareOriginEvenWhenProviderReportIdsChange(AudioAnnouncementMode mode, bool escalationSound)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(mode);
        Assert.IsTrue(policy.Evaluate(Quake(1, JmaScale.Three), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Quake(2, JmaScale.Three, QuakeIssueType.DetailScale), audio).ShouldPlay);
        Assert.AreEqual(escalationSound, policy.Evaluate(Quake(3, JmaScale.Four), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Quake(4, JmaScale.Four), audio).ShouldPlay);
    }

    [TestMethod]
    [DataRow(AudioAnnouncementMode.FirstAnnouncementOnly, false)]
    [DataRow(AudioAnnouncementMode.NewAnnouncementOrEscalation, true)]
    public void QuakeNewRegionCanBeEnabledSeparatelyFromFirstOnly(AudioAnnouncementMode mode, bool addedSound)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(mode);
        Assert.IsTrue(policy.Evaluate(Quake(1, JmaScale.Three), audio).ShouldPlay);
        QuakeEvent extended = Quake(2, JmaScale.Three);
        extended = extended.WithDisplayObservations([.. extended.Points,
            new QuakePoint("宮城県", "仙台市", false, JmaScale.Three, "宮城県仙台市")], null);
        extended = extended with { Signature = new EventSignatureBuilder().Build(extended) };
        Assert.AreEqual(addedSound, policy.Evaluate(extended, audio).ShouldPlay);
    }

    [TestMethod]
    public void FilteredQuakeDoesNotSoundForRegionAdditionOutsideSelectedPrefecture()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        Assert.IsTrue(policy.Evaluate(Quake(1, JmaScale.Three), audio).ShouldPlay);
        QuakeEvent extended = Quake(2, JmaScale.Three);
        extended = extended.WithDisplayObservations([.. extended.Points,
            new QuakePoint("宮城県", "仙台市", false, JmaScale.Three, "宮城県仙台市")], null);
        extended = extended with { Signature = new EventSignatureBuilder().Build(extended) };
        policy.Observe(extended);
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            QuakePrefectureCodes = ["13"],
            QuakePrefectureMode = QuakePrefectureFilterMode.EventAndPoints,
        };
        DisasterEvent? filtered = EventDisplayFilter.Apply(filter, extended);
        Assert.IsNotNull(filtered);
        Assert.IsFalse(policy.Evaluate(filtered, audio).ShouldPlay);
    }

    [TestMethod]
    public void NewEarthquakeSoundsAgainAndUnknownHypocenterUpdatesDoNotRearmPreviousQuake()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.FirstAnnouncementOnly);
        Assert.IsTrue(policy.Evaluate(Quake(1, JmaScale.Three), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Quake(2, JmaScale.Unknown, QuakeIssueType.Destination), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Quake(3, JmaScale.Three), audio).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(Quake(4, JmaScale.Three, origin: Now.AddMinutes(1)), audio).ShouldPlay);
    }

    [TestMethod]
    public void UnreportedFiveLowerOrMoreIsNotAnObservedEscalationFromFiveLower()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        Assert.IsTrue(policy.Evaluate(Quake(1, JmaScale.FiveLower), audio).ShouldPlay);
        QuakeEvent unreported = Quake(2, JmaScale.FiveLowerOrMore);
        Assert.IsFalse(policy.Evaluate(unreported, audio).ShouldPlay);
        Assert.AreEqual(JmaScale.FiveLowerOrMore, unreported.Earthquake.MaximumScale);
        Assert.IsTrue(policy.Evaluate(Quake(3, JmaScale.FiveUpper), audio).ShouldPlay);
    }

    [TestMethod]
    [DataRow(AudioAnnouncementMode.FirstAnnouncementOnly, false)]
    [DataRow(AudioAnnouncementMode.NewAnnouncementOrEscalation, true)]
    public void TsunamiEscalationAndRegionAdditionRespectSelectedMode(AudioAnnouncementMode mode, bool extraSound)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(mode);
        Assert.AreEqual(AudioCueId.TsunamiAdvisory, policy.Evaluate(Tsunami(1, TsunamiGrade.Watch), audio).Cue);
        Assert.IsFalse(policy.Evaluate(Tsunami(2, TsunamiGrade.Watch), audio).ShouldPlay);
        Assert.AreEqual(extraSound, policy.Evaluate(Tsunami(3, TsunamiGrade.Warning), audio).ShouldPlay);
        Assert.AreEqual(extraSound, policy.Evaluate(Tsunami(4, TsunamiGrade.MajorWarning), audio).ShouldPlay);
        TsunamiEvent extended = Tsunami(5, TsunamiGrade.MajorWarning, ["地域A", "地域B"]);
        Assert.AreEqual(extraSound, policy.Evaluate(extended, audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(extended, audio).ShouldPlay);
    }

    [TestMethod]
    public void NewTsunamiAreaChoosesItsGradeInsteadOfHigherContinuingGrade()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        Assert.IsTrue(policy.Evaluate(Tsunami(1, TsunamiGrade.MajorWarning), audio).ShouldPlay);
        TsunamiEvent mixed = Tsunami(2, TsunamiGrade.MajorWarning, ["地域A", "地域B"]);
        mixed = new TsunamiEvent(mixed.Id, mixed.Provider, mixed.IssuedAt, mixed.ReceivedAt, "mixed",
            mixed.SourceMode, mixed.Issue,
            [mixed.Areas[0], mixed.Areas[1] with { Grade = TsunamiGrade.Warning }], false, null);
        Assert.AreEqual(AudioCueId.TsunamiWarning, policy.Evaluate(mixed, audio).Cue);
    }

    [TestMethod]
    [DataRow("VTSE51")]
    [DataRow("VTSE52")]
    public void TsunamiObservationAndItsCancellationNeverAnnounceExistingWarnings(string rawType)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        Assert.IsFalse(policy.Evaluate(Tsunami(1, TsunamiGrade.MajorWarning, rawType: rawType), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(2, TsunamiGrade.MajorWarning, rawType: rawType, cancelled: true), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(3, TsunamiGrade.MajorWarning), audio).ShouldPlay);
    }

    [TestMethod]
    public void TsunamiFullReleaseRearmsNewAnnouncementButDowngradeDoesNotSound()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.FirstAnnouncementOnly);
        Assert.IsTrue(policy.Evaluate(Tsunami(1, TsunamiGrade.MajorWarning), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(2, TsunamiGrade.Warning), audio).ShouldPlay);
        policy.Observe(Tsunami(3, TsunamiGrade.Unknown, [], cancelled: true));
        Assert.IsTrue(policy.Evaluate(Tsunami(4, TsunamiGrade.Warning), audio).ShouldPlay);
    }

    [TestMethod]
    [DataRow(AudioAnnouncementMode.FirstAnnouncementOnly)]
    [DataRow(AudioAnnouncementMode.NewAnnouncementOrEscalation)]
    public void WeatherContinuationAndSameLevelUpdateAreSilentEvenOnFirstReception(AudioAnnouncementMode mode)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(mode);
        Assert.IsFalse(policy.Evaluate(Weather(1, [Item("A", WeatherWarningLevel.SpecialWarning, "継続")]), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Weather(2, [Item("A", WeatherWarningLevel.SpecialWarning, "更新")]), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Weather(3, [Item("A", WeatherWarningLevel.SpecialWarning, "発表中")]), audio).ShouldPlay);
    }

    [TestMethod]
    [DataRow(AudioAnnouncementMode.FirstAnnouncementOnly, false)]
    [DataRow(AudioAnnouncementMode.NewAnnouncementOrEscalation, true)]
    public void WeatherEscalationAndNewAreaRespectModeButSameLevelDoesNot(AudioAnnouncementMode mode, bool extraSound)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(mode);
        Assert.AreEqual(AudioCueId.WeatherWarning, policy.Evaluate(Weather(1, [Item("A", WeatherWarningLevel.Warning)]), audio).Cue);
        Assert.IsFalse(policy.Evaluate(Weather(2, [Item("A", WeatherWarningLevel.Warning, "更新")]), audio).ShouldPlay);
        Assert.AreEqual(extraSound, policy.Evaluate(Weather(3, [Item("A", WeatherWarningLevel.SpecialWarning, "切替")]), audio).ShouldPlay);
        AudioDecision addition = policy.Evaluate(Weather(4,
            [Item("A", WeatherWarningLevel.SpecialWarning, "継続"), Item("B", WeatherWarningLevel.Warning)]), audio);
        Assert.AreEqual(extraSound, addition.ShouldPlay);
        if (extraSound) Assert.AreEqual(AudioCueId.WeatherWarning, addition.Cue);
    }

    [TestMethod]
    public void WeatherReleaseReceivedWhileHiddenRearmsFreshWarningWithoutSoundingRelease()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.FirstAnnouncementOnly);
        Assert.IsTrue(policy.Evaluate(Weather(1, [Item("A", WeatherWarningLevel.SpecialWarning)]), audio).ShouldPlay);
        WeatherWarningEvent release = Weather(2, [Item("A", WeatherWarningLevel.SpecialWarning, "解除", active: false)], cancelled: true);
        policy.Observe(release);
        Assert.IsFalse(policy.Evaluate(release, audio).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(Weather(3, [Item("A", WeatherWarningLevel.SpecialWarning)]), audio).ShouldPlay);
    }

    [TestMethod]
    public void FilteredWeatherDoesNotSoundForAnAnnouncementOutsideSelectedArea()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        WeatherWarningEvent initial = Weather(1, [Item("A", WeatherWarningLevel.Warning, "継続")]);
        policy.Observe(initial);
        WeatherWarningEvent mixed = Weather(2,
            [Item("A", WeatherWarningLevel.Warning, "継続"), Item("B", WeatherWarningLevel.SpecialWarning)]);
        policy.Observe(mixed);
        Assert.IsFalse(policy.Evaluate(mixed.WithItems([mixed.Items[0]]), audio).ShouldPlay);
    }

    [TestMethod]
    public void MutedReportsAndCorrectionsUpdateBaselineWithoutDelayedAudio()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        TsunamiEvent first = Tsunami(1, TsunamiGrade.Warning);
        Assert.IsFalse(policy.Evaluate(first, audio with { Muted = true }).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(first, audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(2, TsunamiGrade.Warning), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(3, TsunamiGrade.MajorWarning, correction: true), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(4, TsunamiGrade.MajorWarning), audio).ShouldPlay);
    }

    [TestMethod]
    public void LateReportCannotDowngradeBaselineAndCreateFalseEscalation()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation);
        Assert.IsTrue(policy.Evaluate(Tsunami(1, TsunamiGrade.Warning), audio).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(Tsunami(4, TsunamiGrade.MajorWarning), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(2, TsunamiGrade.Warning), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(5, TsunamiGrade.MajorWarning), audio).ShouldPlay);
    }

    [TestMethod]
    [DataRow(SourceMode.Sandbox)]
    [DataRow(SourceMode.ManualTest)]
    [DataRow(SourceMode.HistoryRehearsal)]
    public void TrainingCannotConsumeProductionFirstAnnouncement(SourceMode training)
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.FirstAnnouncementOnly);
        Assert.IsTrue(policy.Evaluate(Tsunami(1, TsunamiGrade.Warning, sourceMode: training), audio).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(Tsunami(1, TsunamiGrade.Warning), audio).ShouldPlay);
        Assert.IsFalse(policy.Evaluate(Tsunami(2, TsunamiGrade.Warning), audio).ShouldPlay);
    }

    [TestMethod]
    public void ExistingModeKeepsManualTestPlaybackAndEewReportSelection()
    {
        var policy = new AudioPolicy();
        AudioSettings audio = Settings(AudioAnnouncementMode.Legacy);
        TsunamiEvent manual = Tsunami(1, TsunamiGrade.Warning, sourceMode: SourceMode.ManualTest);
        Assert.IsTrue(policy.Evaluate(manual, audio).ShouldPlay);
        Assert.IsTrue(policy.Evaluate(manual, audio).ShouldPlay);
        Assert.AreEqual(AudioAnnouncementMode.Legacy, Settings(AudioAnnouncementMode.FirstAnnouncementOnly).GetAnnouncementMode(EventKind.Eew));
    }

    [TestMethod]
    public void ExpiredWarningDoesNotBecomeNewAnnouncement()
    {
        var policy = new AudioPolicy();
        Assert.IsFalse(policy.Evaluate(Tsunami(1, TsunamiGrade.MajorWarning) with { IsExpired = true },
            Settings(AudioAnnouncementMode.NewAnnouncementOrEscalation)).ShouldPlay);
    }

    private static AudioSettings Settings(AudioAnnouncementMode mode) => AudioSettings.Disabled with
    {
        QuakeAnnouncementMode = mode, TsunamiAnnouncementMode = mode, WeatherAnnouncementMode = mode,
        QuakeEnabled = true, QuakeFilePath = "quake.wav",
        TsunamiAdvisoryEnabled = true, TsunamiAdvisoryFilePath = "watch.wav",
        TsunamiWarningEnabled = true, TsunamiWarningFilePath = "warning.wav",
        TsunamiMajorWarningEnabled = true, TsunamiMajorWarningFilePath = "major.wav",
        WeatherWarningEnabled = true, WeatherWarningFilePath = "weather.wav",
        WeatherSpecialWarningEnabled = true, WeatherSpecialWarningFilePath = "special.wav",
    };

    private static QuakeEvent Quake(int report, JmaScale maximum, QuakeIssueType type = QuakeIssueType.ScalePrompt,
        DateTimeOffset? origin = null)
    {
        DateTimeOffset issued = Now.AddSeconds(report);
        var quake = new QuakeEvent(EventId.Create("quake-report-" + report), "test", issued, issued, "", SourceMode.Production,
            new IssueInfo("気象庁", issued, type.ToString(), CorrectionType.None), type,
            new EarthquakeInfo(origin ?? Now, null, null, maximum, DomesticTsunami.None, ForeignTsunami.None),
            maximum == JmaScale.Unknown ? [] : [new QuakePoint("東京都", "千代田区", false, maximum, "東京都千代田区")], "");
        return quake with { Signature = new EventSignatureBuilder().Build(quake) };
    }

    private static TsunamiEvent Tsunami(int report, TsunamiGrade grade, string[]? areas = null,
        string rawType = "VTSE41", bool cancelled = false, bool correction = false, SourceMode sourceMode = SourceMode.Production)
    {
        DateTimeOffset issued = Now.AddSeconds(report);
        var tsunami = new TsunamiEvent(EventId.Create("tsunami-report-" + report), "test", issued, issued, "", sourceMode,
            new IssueInfo("気象庁", issued, rawType, correction ? CorrectionType.Generic : CorrectionType.None),
            (areas ?? ["地域A"]).Select(name => new TsunamiArea(grade, false, name, null, null)).ToArray(), cancelled, null);
        return tsunami with { Signature = new EventSignatureBuilder().Build(tsunami) };
    }

    private static WeatherWarningItem Item(string area, WeatherWarningLevel level, string status = "発表", bool active = true) =>
        new(area, area, level == WeatherWarningLevel.SpecialWarning ? "大雨特別警報" : "大雨警報", "03", level, status, active);

    private static WeatherWarningEvent Weather(int report, WeatherWarningItem[] items, bool cancelled = false)
    {
        DateTimeOffset issued = Now.AddSeconds(report);
        var weather = new WeatherWarningEvent(EventId.Create("weather-report-" + report), "test", issued, issued, "", SourceMode.Production,
            new IssueInfo("東京管区気象台", issued, "VPWW56", CorrectionType.None), "気象警報・注意報", items, cancelled);
        return weather with { Signature = new EventSignatureBuilder().Build(weather) };
    }
}
