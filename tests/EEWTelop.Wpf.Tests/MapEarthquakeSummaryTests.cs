using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class MapEarthquakeSummaryTests
{
    private static QuakeEvent Create(EarthquakeInfo info, bool cancelled = false)
    {
        var original = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow).Single(s => s.Id == "detail-scale").Event;
        return new QuakeEvent(original.Id, original.Provider, original.IssuedAt, original.ReceivedAt, original.Signature,
            original.SourceMode, original.Issue, original.IssueType, info, original.Points, "", isCancelled: cancelled);
    }

    [TestMethod]
    public void TsunamiUnknownCheckingAndForeignAreNotReportedAsNone()
    {
        var info = new EarthquakeInfo(DateTimeOffset.UtcNow, null, null, JmaScale.Unknown, DomesticTsunami.Unknown, ForeignTsunami.Unknown);
        var unknown = MapEarthquakeSummary.Create(Create(info));
        Assert.AreEqual("M不明", unknown.Magnitude);
        Assert.AreEqual("震源地不明", unknown.Hypocenter);
        Assert.Contains("不明", unknown.MaximumIntensity);
        Assert.AreEqual("日本：津波情報不明", unknown.Tsunami);
        foreach (var value in Enum.GetValues<DomesticTsunami>())
        {
            var summary = MapEarthquakeSummary.Create(Create(info with { DomesticTsunami = value }));
            Assert.IsFalse(string.IsNullOrWhiteSpace(summary.Tsunami));
            if (value != DomesticTsunami.None) Assert.DoesNotContain("津波の心配なし", summary.Tsunami);
        }
        var checking = MapEarthquakeSummary.Create(Create(info with { DomesticTsunami = DomesticTsunami.Checking, ForeignTsunami = ForeignTsunami.WarningPacificWide }));
        Assert.Contains("調査中", checking.Tsunami);
        Assert.Contains("太平洋の広域", checking.Tsunami);
    }

    [TestMethod]
    public void MagnitudeDescriptionAndCancelledInformationArePreserved()
    {
        var info = new EarthquakeInfo(DateTimeOffset.UtcNow, null,
            new HypocenterInfo("テスト震源", "", null, null, null, null, "", "Ｍ８を超える巨大地震"),
            JmaScale.SixUpper, DomesticTsunami.None, ForeignTsunami.Unknown);
        var summary = MapEarthquakeSummary.Create(Create(info));
        Assert.AreEqual("Ｍ８を超える巨大地震", summary.Magnitude);
        Assert.AreEqual("震度6強", summary.MaximumIntensity);
        var cancelled = MapEarthquakeSummary.Create(Create(info, true));
        Assert.AreEqual("表示しません", cancelled.MaximumIntensity);
        Assert.DoesNotContain("心配なし", cancelled.Tsunami);
    }
}
