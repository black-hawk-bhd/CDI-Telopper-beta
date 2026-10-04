using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class QuakeDisplayFilterTests
{
    private static readonly string[] StrongBadges = ["震度5弱以上 未入電", "震度4"];
    private static readonly string[] AllBadges = ["震度5弱以上 未入電", "震度4", "震度2", "震度1"];
    private static readonly string[] TwoBadge = ["震度2"];
    [TestMethod]
    [DataRow(JmaScale.One)]
    [DataRow(JmaScale.Two)]
    [DataRow(JmaScale.Three)]
    [DataRow(JmaScale.Four)]
    [DataRow(JmaScale.FiveLower)]
    [DataRow(JmaScale.FiveUpper)]
    [DataRow(JmaScale.SixLower)]
    [DataRow(JmaScale.SixUpper)]
    public void EachMaximumIntensityCanBeExcludedIndependently(JmaScale scale)
    {
        FilterSettings filter = AppSettings.CreateDefault().Filter with { ExcludedQuakeMaximumScales = [scale] };
        QuakeEvent quake = Create(scale);
        Assert.IsNull(EventDisplayFilter.Apply(filter, quake));
        Assert.IsFalse(EventDisplayFilter.IsEnabled(filter, quake));
        Assert.AreEqual("最大震度フィルター", EventDisplayFilter.DescribeSuppression(filter, quake));
        JmaScale other = scale == JmaScale.One ? JmaScale.Two : JmaScale.One;
        Assert.IsNotNull(EventDisplayFilter.Apply(filter, Create(other)));
    }

    [TestMethod]
    [DataRow(JmaScale.Unknown)]
    [DataRow(JmaScale.FiveLowerOrMore)]
    [DataRow(JmaScale.Seven)]
    public void UnknownUnreportedAndSevenCannotBeExcludedAsOrdinaryIntensity(JmaScale scale)
    {
        FilterSettings filter = AppSettings.CreateDefault().Filter with
        {
            ExcludedQuakeMaximumScales = [.. QuakeFilterOptions.ExcludableScales, scale],
        };
        Assert.IsNotNull(EventDisplayFilter.Apply(filter, Create(scale)));
    }

    [TestMethod]
    public void LegacyTwoClassFilterMigratesButExplicitEmptySelectionOverridesIt()
    {
        var legacy = new FilterSettings(true, true, true, HideQuakeBelowIntensity3: true);
        Assert.IsNull(EventDisplayFilter.Apply(legacy, Create(JmaScale.Two)));
        Assert.IsNotNull(EventDisplayFilter.Apply(legacy, Create(JmaScale.Three)));
        Assert.IsNotNull(EventDisplayFilter.Apply(legacy with { ExcludedQuakeMaximumScales = [] }, Create(JmaScale.Two)));
    }

    [TestMethod]
    [DataRow(QuakePrefectureFilterMode.EventAndPoints, 1)]
    [DataRow(QuakePrefectureFilterMode.EventOnly, 2)]
    [DataRow(QuakePrefectureFilterMode.PointsOnly, 1)]
    public void RegionModesSelectEventsAndOrPointsWithoutChangingSourceMetadata(
        QuakePrefectureFilterMode mode, int visibleCount)
    {
        QuakeEvent quake = Create(JmaScale.Two, JmaScale.FiveUpper) with { IsExpired = true };
        FilterSettings filter = Regional(mode);
        QuakeEvent displayed = (QuakeEvent)EventDisplayFilter.Apply(filter, quake)!;
        Assert.HasCount(visibleCount, displayed.Points);
        Assert.AreEqual(JmaScale.FiveUpper, displayed.Earthquake.MaximumScale);
        Assert.AreEqual(quake.Signature, displayed.Signature);
        Assert.AreEqual(quake.SourceMode, displayed.SourceMode);
        Assert.IsTrue(displayed.IsExpired);
        Assert.HasCount(2, quake.Points);

        QuakeEvent outside = Create(JmaScale.Four, prefecture: "宮城県");
        DisasterEvent? outsideResult = EventDisplayFilter.Apply(filter, outside);
        if (mode == QuakePrefectureFilterMode.PointsOnly)
        {
            Assert.IsNotNull(outsideResult);
            Assert.HasCount(0, ((QuakeEvent)outsideResult).Points);
        }
        else
        {
            Assert.IsNull(outsideResult);
            Assert.AreEqual("地震の都道府県フィルター", EventDisplayFilter.DescribeSuppression(filter, outside));
        }
    }

    [TestMethod]
    public void NationwideDoesNotRestrictAndSeveralPrefecturesAreSupported()
    {
        QuakeEvent quake = Create(JmaScale.Three, JmaScale.Four);
        Assert.AreSame(quake, EventDisplayFilter.Apply(Regional(QuakePrefectureFilterMode.EventAndPoints) with
            { QuakePrefectureCodes = [] }, quake));
        QuakeEvent both = (QuakeEvent)EventDisplayFilter.Apply(Regional(QuakePrefectureFilterMode.EventAndPoints) with
            { QuakePrefectureCodes = ["13", "04"] }, quake)!;
        Assert.HasCount(2, both.Points);
    }

    [TestMethod]
    public void PrefectureResolutionUsesNamesOrMunicipalityButNotSeismicAreaPrefixes()
    {
        Assert.AreEqual("13", QuakeFilterOptions.FindPrefectureCode("東京"));
        Assert.AreEqual("27", QuakeFilterOptions.FindPrefectureCode("大阪府"));
        Assert.AreEqual("01", QuakeFilterOptions.FindPrefectureCode("北海道"));
        Assert.AreEqual("13", QuakeFilterOptions.FindPrefectureCode("", "13101"));
        Assert.IsNull(QuakeFilterOptions.FindPrefectureCode("", "130"));
        Assert.IsNull(QuakeFilterOptions.FindPrefectureCode("東京都沖"));
    }

    [TestMethod]
    public void CancellationAndUnclassifiableTelegramAreNotLostToNewFilters()
    {
        FilterSettings filter = Regional(QuakePrefectureFilterMode.EventAndPoints) with
            { ExcludedQuakeMaximumScales = [JmaScale.Two] };
        QuakeEvent cancel = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale,
            [DisplayEventFactory.Point(1, JmaScale.Two, "宮城県")], isCancelled: true);
        Assert.AreSame(cancel, EventDisplayFilter.Apply(filter, cancel));
        QuakeEvent hypocenterOnly = DisplayEventFactory.CreateQuake(QuakeIssueType.Destination);
        Assert.AreSame(hypocenterOnly, EventDisplayFilter.Apply(filter, hypocenterOnly));
        Assert.IsNull(EventDisplayFilter.Apply(filter with { Quake = false }, cancel));
    }

    [TestMethod]
    public void LongPeriodObservedRegionsAreFilteredWithoutAlteringNationalMaximum()
    {
        var observation = new LongPeriodIntensityInfo(4,
            [new("東京都", "東京都２３区", 1), new("宮城県", "宮城県北部", 4)]);
        QuakeEvent quake = DisplayEventFactory.CreateQuake(QuakeIssueType.LongPeriodObservation,
            longPeriodIntensity: observation);
        QuakeEvent displayed = (QuakeEvent)EventDisplayFilter.Apply(Regional(QuakePrefectureFilterMode.EventAndPoints), quake)!;
        Assert.AreEqual(4, displayed.LongPeriodIntensity!.MaximumClass);
        Assert.HasCount(1, displayed.LongPeriodIntensity.Areas);
        Assert.AreEqual("東京都", displayed.LongPeriodIntensity.Areas[0].Prefecture);
        Assert.HasCount(2, quake.LongPeriodIntensity!.Areas);
    }

    [TestMethod]
    [DataRow(QuakeIssueType.DetailScale)]
    [DataRow(QuakeIssueType.ScaleAndDestination)]
    [DataRow(QuakeIssueType.ScalePrompt)]
    public void LowPointSwitchIncludesBothOneAndTwoInStrongEarthquakes(QuakeIssueType issueType)
    {
        QuakeEvent quake = DisplayEventFactory.CreateQuake(issueType,
            [DisplayEventFactory.Point(1, JmaScale.Four), DisplayEventFactory.Point(2, JmaScale.Two),
             DisplayEventFactory.Point(3, JmaScale.One), DisplayEventFactory.Point(4, JmaScale.FiveLowerOrMore)]);
        DisplaySettings settings = AppSettings.CreateDefault().Display;
        var composer = new PageComposer();
        string[] normal = IntensityBadges(composer.Compose(quake, settings));
        CollectionAssert.AreEqual(StrongBadges, normal);
        string[] expanded = IntensityBadges(composer.Compose(quake, settings with
            { ShowLowIntensityPointsInStrongQuakes = true }));
        CollectionAssert.AreEqual(AllBadges, expanded);
    }

    [TestMethod]
    public void LowPointSwitchKeepsRegionSelectionAndDoesNotChangeWeakEarthquakeLayout()
    {
        var composer = new PageComposer();
        DisplaySettings settings = AppSettings.CreateDefault().Display;
        QuakeEvent displayed = (QuakeEvent)EventDisplayFilter.Apply(Regional(QuakePrefectureFilterMode.EventAndPoints),
            Create(JmaScale.Two, JmaScale.FiveUpper))!;
        Assert.HasCount(0, IntensityBadges(composer.Compose(displayed, settings)));
        CollectionAssert.AreEqual(TwoBadge, IntensityBadges(composer.Compose(displayed, settings with
            { ShowLowIntensityPointsInStrongQuakes = true })));
        QuakeEvent weak = Create(JmaScale.Two, JmaScale.One);
        CollectionAssert.AreEqual(IntensityBadges(composer.Compose(weak, settings)),
            IntensityBadges(composer.Compose(weak, settings with { ShowLowIntensityPointsInStrongQuakes = true })));
    }

    private static string[] IntensityBadges(DisplayProgram program) => program.Pages
        .SelectMany(page => page.Blocks).Where(block => block.StyleToken == DisplayStyleTokens.Intensity)
        .Select(block => block.Badge).Where(badge => badge.Length > 0).ToArray();

    private static QuakeEvent Create(JmaScale scale, JmaScale? other = null, string prefecture = "東京都") =>
        DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale, other.HasValue
            ? [DisplayEventFactory.Point(1, scale, prefecture), DisplayEventFactory.Point(2, other.Value, "宮城県")]
            : [DisplayEventFactory.Point(1, scale, prefecture)]);

    private static FilterSettings Regional(QuakePrefectureFilterMode mode) =>
        AppSettings.CreateDefault().Filter with { QuakePrefectureCodes = ["13"], QuakePrefectureMode = mode };
}
